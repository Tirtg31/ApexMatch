using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Api.BackgroundServices;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Interfaces.Repositories.MarketData;
using TradingEngine.Infrastructure.MarketData;
using Xunit;

namespace TradingEngine.Tests.MarketData;

public sealed class MarketPriceBackgroundServiceTests
{
    [Fact]
    public async Task StartAsync_FetchesActiveInstruments_AndCachesPrices()
    {
        var requested = new ConcurrentBag<string>();
        using var setup = CreateSetup(
            [Instrument("AAPL"), Instrument("SKIP", active: false), Instrument("MSFT")],
            (symbol, _) =>
            {
                requested.Add(symbol);
                return Task.FromResult<MarketPrice?>(Price(symbol, 150m));
            });

        await setup.Worker.StartAsync(CancellationToken.None);
        try
        {
            await UntilAsync(() => setup.Store.GetAllPrices().Count == 2);

            Assert.Contains("AAPL", requested);
            Assert.Contains("MSFT", requested);
            Assert.DoesNotContain("SKIP", requested);
            Assert.True(setup.Store.TryGetPrice("AAPL", out var aapl));
            Assert.Equal(150m, aapl!.Price);
        }
        finally { await setup.Worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task StartAsync_NullQuote_DoesNotOverwriteLastKnownPrice()
    {
        using var setup = CreateSetup(
            [Instrument("AAPL")],
            (_, _) => Task.FromResult<MarketPrice?>(null));
        setup.Store.UpdatePrice(Price("AAPL", 125m));

        await setup.Worker.StartAsync(CancellationToken.None);
        try
        {
            await UntilAsync(() => setup.Requests.Contains("AAPL"));
            // Wait until the refresh scope is disposed (after the null quote is processed).
            await UntilAsync(() => setup.DisposedScopes.Count > 0);
            Assert.True(setup.Store.TryGetPrice("AAPL", out var result));
            Assert.Equal(125m, result!.Price);
        }
        finally { await setup.Worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task StartAsync_Http500ForOneSymbol_ContinuesToNextSymbol()
    {
        using var setup = CreateSetup(
            [Instrument("AAPL"), Instrument("MSFT")],
            (symbol, _) => symbol == "AAPL"
                ? Task.FromException<MarketPrice?>(
                    new HttpRequestException("Provider failed", null,
                        HttpStatusCode.InternalServerError))
                : Task.FromResult<MarketPrice?>(Price(symbol, 200m)));

        await setup.Worker.StartAsync(CancellationToken.None);
        try
        {
            await UntilAsync(() => setup.Store.TryGetPrice("MSFT", out _));
            Assert.False(setup.Store.TryGetPrice("AAPL", out _));
            Assert.Contains("AAPL", setup.Requests);
            Assert.Contains("MSFT", setup.Requests);
        }
        finally { await setup.Worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task StartAsync_Http429_EndsCurrentRefreshCycle()
    {
        using var setup = CreateSetup(
            [Instrument("AAPL"), Instrument("MSFT")],
            (_, _) => Task.FromException<MarketPrice?>(
                new HttpRequestException("Rate limited", null,
                    HttpStatusCode.TooManyRequests)));

        await setup.Worker.StartAsync(CancellationToken.None);
        try
        {
            await UntilAsync(() => setup.DisposedScopes.Count > 0);
            Assert.Equal(new[] { "AAPL" }, setup.Requests.ToArray());
            Assert.Empty(setup.Store.GetAllPrices());
        }
        finally { await setup.Worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task StopAsync_CancelsInProgressProviderCall()
    {
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var setup = CreateSetup(
            [Instrument("AAPL")],
            async (_, ct) =>
            {
                entered.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cancelled.TrySetResult(true);
                    throw;
                }
                return Price("AAPL", 100m);
            });

        await setup.Worker.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await setup.Worker.StopAsync(stopCts.Token);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Refresh_DisposesDependencyInjectionScope()
    {
        using var setup = CreateSetup(
            [Instrument("AAPL")],
            (symbol, _) => Task.FromResult<MarketPrice?>(Price(symbol, 100m)));

        await setup.Worker.StartAsync(CancellationToken.None);
        try
        {
            await UntilAsync(() => setup.DisposedScopes.Count > 0);
            Assert.Single(setup.DisposedScopes);
        }
        finally { await setup.Worker.StopAsync(CancellationToken.None); }
    }

    private static TradeInstrument Instrument(string symbol, bool active = true) => new()
    {
        InstrumentId = 1,
        InstrumentName = symbol,
        InstrumentSymbol = symbol,
        IsActive = active
    };

    private static MarketPrice Price(string symbol, decimal price) => new()
    {
        InstrumentSymbol = symbol,
        Price = price,
        LastUpdatedUtc = DateTimeOffset.UtcNow,
        Provider = "FakeFinnhub"
    };

    private static Setup CreateSetup(
        List<TradeInstrument> instruments,
        Func<string, CancellationToken, Task<MarketPrice?>> quote)
    {
        var requests = new ConcurrentQueue<string>();
        var disposedScopes = new ConcurrentBag<int>();
        var services = new ServiceCollection();
        services.AddScoped(_ => new ScopeMarker(disposedScopes));
        services.AddScoped<IInstrumentRepository>(sp =>
        {
            _ = sp.GetRequiredService<ScopeMarker>();
            return new FakeInstrumentRepository(instruments);
        });
        services.AddTransient<IMarketDataProvider>(_ => new FakeMarketDataProvider(
            (symbol, ct) =>
            {
                requests.Enqueue(symbol);
                return quote(symbol, ct);
            }));
        services.AddSingleton<IMarketPriceStore, MarketPriceStore>();
        var root = services.BuildServiceProvider(validateScopes: true);
        var store = root.GetRequiredService<IMarketPriceStore>();
        var worker = new MarketPriceBackgroundService(
            root.GetRequiredService<IServiceScopeFactory>(),
            store,
            NullLogger<MarketPriceBackgroundService>.Instance);
        return new Setup(root, worker, store, requests, disposedScopes);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed record Setup(
        ServiceProvider Root,
        MarketPriceBackgroundService Worker,
        IMarketPriceStore Store,
        ConcurrentQueue<string> Requests,
        ConcurrentBag<int> DisposedScopes) : IDisposable
    {
        public void Dispose()
        {
            Worker.Dispose();
            Root.Dispose();
        }
    }

    private sealed class ScopeMarker(ConcurrentBag<int> disposedScopes) : IDisposable
    {
        public void Dispose() => disposedScopes.Add(1);
    }

    private sealed class FakeInstrumentRepository(List<TradeInstrument> instruments)
        : IInstrumentRepository
    {
        public Task<List<TradeInstrument>> GetAllAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(instruments);
        }

        public Task<TradeInstrument?> GetByIdAsync(
            int instrumentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<TradeInstrument?>(
                instruments.FirstOrDefault(i => i.InstrumentId == instrumentId));
        }
    }

    private sealed class FakeMarketDataProvider(
        Func<string, CancellationToken, Task<MarketPrice?>> quote) : IMarketDataProvider
    {
        public Task<MarketPrice?> GetCurrentPriceAsync(
            string instrumentSymbol, CancellationToken ct = default) =>
            quote(instrumentSymbol, ct);
    }
}
