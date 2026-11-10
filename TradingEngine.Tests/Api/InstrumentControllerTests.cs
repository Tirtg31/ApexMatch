using Microsoft.AspNetCore.Mvc;
using TradingEngine.Api.Controllers;
using TradingEngine.Api.DTOs.Instrument;
using TradingEngine.Api.DTOs;
using TradingEngine.Core.Interfaces.Repositories.MarketData;
using TradingEngine.Infrastructure.MarketData;
using TradingEngine.Core.Entities;
using Xunit;

namespace TradingEngine.Tests.Api;

/// <summary>
/// Unit tests: no Oracle or ASP.NET Core HTTP host required.
/// Calls controller actions directly, so routing/model binding are not exercised.
/// </summary>
public sealed class InstrumentControllerTests
{
    private static TradeInstrument Instrument(int id, string symbol, bool active = true) => new()
    {
        InstrumentId = id,
        InstrumentName = $"Company {symbol}",
        InstrumentSymbol = symbol,
        IsActive = active
    };

    [Fact]
    public async Task GetAllInstruments_WhenRepositoryEmpty_Returns200WithEmptyList()
    {
        var repo = new FakeInstrumentRepository();
        var controller = new InstrumentController(repo, new MarketPriceStore());

        var result = await controller.GetAllInstruments();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(Assert.IsType<List<InstrumentResponseDto>>(ok.Value));
        Assert.Equal(1, repo.GetAllCalls);
    }

    [Fact]
    public async Task GetAllInstruments_MapsEveryInstrumentIncludingInactive()
    {
        var repo = new FakeInstrumentRepository(
            Instrument(10, "AAPL"), Instrument(20, "MSFT", active: false));
        var controller = new InstrumentController(repo, new MarketPriceStore());

        var result = await controller.GetAllInstruments();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var items = Assert.IsType<List<InstrumentResponseDto>>(ok.Value);
        Assert.Collection(items,
            x =>
            {
                Assert.Equal(10, x.InstrumentId);
                Assert.Equal("Company AAPL", x.InstrumentName);
                Assert.Equal("AAPL", x.InstrumentSymbol);
                Assert.True(x.IsActive);
            },
            x =>
            {
                Assert.Equal(20, x.InstrumentId);
                Assert.Equal("Company MSFT", x.InstrumentName);
                Assert.Equal("MSFT", x.InstrumentSymbol);
                Assert.False(x.IsActive);
            });
    }

    [Fact]
    public async Task GetInstrument_Existing_Returns200WithCorrectDto()
    {
        var repo = new FakeInstrumentRepository(Instrument(72, "TSLA", active: false));
        var controller = new InstrumentController(repo, new MarketPriceStore());

        var result = await controller.GetInstrument(72, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<InstrumentResponseDto>(ok.Value);
        Assert.Equal(72, dto.InstrumentId);
        Assert.Equal("Company TSLA", dto.InstrumentName);
        Assert.Equal("TSLA", dto.InstrumentSymbol);
        Assert.False(dto.IsActive);
        Assert.Equal(72, repo.LastRequestedId);
        Assert.Equal(1, repo.GetByIdCalls);
    }

    [Fact]
    public async Task GetInstrument_Missing_Returns404()
    {
        var controller = new InstrumentController(new FakeInstrumentRepository(), new MarketPriceStore());

        var result = await controller.GetInstrument(999999, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Contains("999999", Assert.IsType<string>(notFound.Value));
    }

    [Fact]
    public async Task GetInstrument_PassesCorrectIdAndCancellationToken()
    {
        var repo = new FakeInstrumentRepository(Instrument(5, "IBM"));
        using var cts = new CancellationTokenSource();
        var controller = new InstrumentController(repo, new MarketPriceStore());

        await controller.GetInstrument(5, cts.Token);

        Assert.Equal(5, repo.LastRequestedId);
        Assert.Equal(cts.Token, repo.LastToken);
    }

    [Fact]
    public async Task GetInstrument_RepositoryFailure_PropagatesException()
    {
        var repo = new FakeInstrumentRepository { Failure = new InvalidOperationException("DB unavailable") };
        var controller = new InstrumentController(repo, new MarketPriceStore());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.GetInstrument(10, CancellationToken.None));

        Assert.Equal("DB unavailable", ex.Message);
    }

    [Fact]
    public async Task GetAllInstruments_RepositoryFailure_PropagatesException()
    {
        var repo = new FakeInstrumentRepository { Failure = new InvalidOperationException("DB unavailable") };
        var controller = new InstrumentController(repo, new MarketPriceStore());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.GetAllInstruments());

        Assert.Equal("DB unavailable", ex.Message);
    }

    [Fact]
    public async Task GetAllInstruments_DoesNotMutateDomainEntity()
    {
        var instrument = Instrument(11, "NFLX");
        var repo = new FakeInstrumentRepository(instrument);
        var controller = new InstrumentController(repo, new MarketPriceStore());

        var result = await controller.GetAllInstruments();
        var dto = Assert.Single(Assert.IsType<List<InstrumentResponseDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value));
        dto.InstrumentName = "Edited in DTO";

        Assert.Equal("Company NFLX", instrument.InstrumentName);
    }

    private static MarketPrice Price(string symbol, decimal amount = 187.45m) => new()
    {
        InstrumentSymbol = symbol,
        Price = amount,
        Provider = "Finnhub",
        LastUpdatedUtc = DateTimeOffset.FromUnixTimeSeconds(1710000000)
    };

    [Fact]
    public async Task GetPrice_ValidInstrument_Returns200AndCorrectDto()
    {
        var repo = new FakeInstrumentRepository(Instrument(7, "AAPL"));
        var store = new MarketPriceStore();
        store.UpdatePrice(Price("AAPL", 200.25m));
        var controller = new InstrumentController(repo, store);

        var action = await controller.GetPrice(7, CancellationToken.None);

        var dto = Assert.IsType<MarketPriceResponseDto>(
            Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.Equal(7, dto.InstrumentId);
        Assert.Equal("AAPL", dto.InstrumentSymbol);
        Assert.Equal(200.25m, dto.Price);
        Assert.Equal("Finnhub", dto.Provider);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1710000000), dto.LastUpdatedUtc);
        Assert.Equal(1, repo.GetByIdCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetPrice_InvalidId_Returns400WithoutQueryingRepository(int id)
    {
        var repo = new FakeInstrumentRepository();
        var controller = new InstrumentController(repo, new MarketPriceStore());

        var action = await controller.GetPrice(id, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(0, repo.GetByIdCalls);
    }

    [Fact]
    public async Task GetPrice_MissingInstrument_Returns404()
    {
        var repo = new FakeInstrumentRepository();
        var controller = new InstrumentController(repo, new MarketPriceStore());

        var action = await controller.GetPrice(99, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(action.Result);
        Assert.Contains("99", Assert.IsType<string>(notFound.Value));
    }

    [Fact]
    public async Task GetPrice_UncachedSymbol_Returns503()
    {
        var repo = new FakeInstrumentRepository(Instrument(12, "MSFT"));
        var store = new MarketPriceStore();
        store.UpdatePrice(Price("AAPL"));
        var controller = new InstrumentController(repo, store);

        var action = await controller.GetPrice(12, CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(503, result.StatusCode);
        Assert.Contains("MSFT", Assert.IsType<string>(result.Value));
    }

    [Fact]
    public async Task GetPrice_PassesCancellationTokenToRepository()
    {
        var repo = new FakeInstrumentRepository(Instrument(5, "IBM"));
        using var cts = new CancellationTokenSource();
        var controller = new InstrumentController(repo, new MarketPriceStore());

        await controller.GetPrice(5, cts.Token);

        Assert.Equal(5, repo.LastRequestedId);
        Assert.Equal(cts.Token, repo.LastToken);
    }

    [Fact]
    public async Task GetPrice_RepositoryError_Propagates()
    {
        var repo = new FakeInstrumentRepository
        {
            Failure = new InvalidOperationException("DB down")
        };
        var controller = new InstrumentController(repo, new MarketPriceStore());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.GetPrice(5, CancellationToken.None));

        Assert.Equal("DB down", error.Message);
    }

    [Fact]
    public void GetAllPrices_EmptyStore_ReturnsEmptyArrayWithoutOracleAccess()
    {
        var repo = new FakeInstrumentRepository();
        var controller = new InstrumentController(repo, new MarketPriceStore());

        var action = controller.GetAllPrices();

        var result = Assert.IsType<OkObjectResult>(action.Result);
        var prices = Assert.IsAssignableFrom<IReadOnlyCollection<MarketPriceResponseDto>>(result.Value);
        Assert.Empty(prices);
        Assert.Equal(0, repo.GetAllCalls);
        Assert.Equal(0, repo.GetByIdCalls);
    }

    [Fact]
    public void GetAllPrices_MultipleQuotes_MapsWithoutOracleAccess()
    {
        var repo = new FakeInstrumentRepository();
        var store = new MarketPriceStore();
        store.UpdatePrice(Price("AAPL", 190m));
        store.UpdatePrice(Price("MSFT", 420m));
        var controller = new InstrumentController(repo, store);

        var action = controller.GetAllPrices();

        var result = Assert.IsType<OkObjectResult>(action.Result);
        var prices = Assert.IsAssignableFrom<IReadOnlyCollection<MarketPriceResponseDto>>(result.Value);
        Assert.Equal(2, prices.Count);
        var apple = Assert.Single(prices.Where(p => p.InstrumentSymbol == "AAPL"));
        Assert.Equal(190m, apple.Price);
        Assert.Null(apple.InstrumentId);
        Assert.Equal("Finnhub", apple.Provider);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1710000000), apple.LastUpdatedUtc);
        var microsoft = Assert.Single(prices.Where(p => p.InstrumentSymbol == "MSFT"));
        Assert.Equal(420m, microsoft.Price);
        Assert.Null(microsoft.InstrumentId);
        Assert.Equal(0, repo.GetAllCalls);
        Assert.Equal(0, repo.GetByIdCalls);
    }

    [Fact]
    public void GetAllPrices_UpdatedQuote_ReturnsLatestSnapshot()
    {
        var store = new MarketPriceStore();
        store.UpdatePrice(Price("AAPL", 100m));
        store.UpdatePrice(Price("AAPL", 125m));
        var controller = new InstrumentController(new FakeInstrumentRepository(), store);

        var action = controller.GetAllPrices();

        var prices = Assert.IsAssignableFrom<IReadOnlyCollection<MarketPriceResponseDto>>(
            Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.Equal(125m, Assert.Single(prices).Price);
    }

    private sealed class FakeInstrumentRepository : IInstrumentRepository
    {
        private readonly List<TradeInstrument> instruments;

        public FakeInstrumentRepository(params TradeInstrument[] instruments)
        {
            this.instruments = instruments.ToList();
        }

        public int GetAllCalls { get; private set; }
        public int GetByIdCalls { get; private set; }
        public int? LastRequestedId { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public Exception? Failure { get; set; }

        public Task<List<TradeInstrument>> GetAllAsync(CancellationToken ct = default)
        {
            GetAllCalls++;
            if (Failure is not null) return Task.FromException<List<TradeInstrument>>(Failure);
            return Task.FromResult(instruments.ToList());
        }

        public Task<TradeInstrument?> GetByIdAsync(int instrumentId, CancellationToken ct = default)
        {
            GetByIdCalls++;
            LastRequestedId = instrumentId;
            LastToken = ct;
            if (Failure is not null) return Task.FromException<TradeInstrument?>(Failure);
            return Task.FromResult(instruments.FirstOrDefault(x => x.InstrumentId == instrumentId));
        }
    }
}
