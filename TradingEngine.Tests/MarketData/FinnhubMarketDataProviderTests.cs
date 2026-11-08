
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Core.Entities;
using TradingEngine.Infrastructure.MarketData;
using Xunit;

namespace TradingEngine.Tests.MarketData;

public sealed class FinnhubMarketDataProviderTests
{
    private const string ValidQuote =
        """
        {
            "c": 187.45,
            "d": 1.25,
            "dp": 0.67,
            "h": 188.50,
            "l": 185.20,
            "o": 186.00,
            "pc": 186.20,
            "t": 1710000000
        }
        """;

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json")
        };
    }

    private static FinnhubMarketDataProvider CreateProvider(
        Func<HttpRequestMessage,
             CancellationToken,
             Task<HttpResponseMessage>> responder)
    {
        var handler = new FakeHttpMessageHandler(responder);

        var client = new HttpClient(handler)
        {
            BaseAddress =
                new Uri("https://finnhub.io/api/v1/")
        };

        return new FinnhubMarketDataProvider(
            client,
            NullLogger<FinnhubMarketDataProvider>.Instance);
    }

    // TEST 1: Successful quote retrieval and mapping

    [Fact]
    public async Task GetCurrentPriceAsync_ValidQuote_ReturnsMarketPrice()
    {
        var provider = CreateProvider(
            (_, _) => Task.FromResult(
                JsonResponse(ValidQuote)));

        MarketPrice? result =
            await provider.GetCurrentPriceAsync("AAPL");

        Assert.NotNull(result);
        Assert.Equal("AAPL", result.InstrumentSymbol);
        Assert.Equal(187.45m, result.Price);
        Assert.Equal("Finnhub", result.Provider);

        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1710000000),
            result.LastUpdatedUtc);
    }

    // TEST 2: Symbol normalization and request URL

    [Fact]
    public async Task GetCurrentPriceAsync_SymbolWithSpaces_NormalizesSymbol()
    {
        Uri? capturedUri = null;

        var provider = CreateProvider((request, _) =>
        {
            capturedUri = request.RequestUri;

            return Task.FromResult(
                JsonResponse(ValidQuote));
        });

        var result =
            await provider.GetCurrentPriceAsync(" aapl ");

        Assert.NotNull(result);
        Assert.Equal("AAPL", result.InstrumentSymbol);

        Assert.NotNull(capturedUri);
        Assert.Equal(
            "https://finnhub.io/api/v1/quote?symbol=AAPL",
            capturedUri.ToString());
    }

    // TEST 3: Invalid input validation

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    public async Task GetCurrentPriceAsync_InvalidSymbol_ThrowsArgumentException(
        string? symbol)
    {
        var requestCount = 0;

        var provider = CreateProvider((_, _) =>
        {
            requestCount++;

            return Task.FromResult(
                JsonResponse(ValidQuote));
        });

        var exception =
            await Assert.ThrowsAsync<ArgumentException>(
                () => provider.GetCurrentPriceAsync(symbol!));

        Assert.Equal("instrumentSymbol", exception.ParamName);
        Assert.Equal(0, requestCount);
    }

    // TEST 4: Null JSON response

    [Fact]
    public async Task GetCurrentPriceAsync_NullJson_ReturnsNull()
    {
        var provider = CreateProvider(
            (_, _) => Task.FromResult(
                JsonResponse("null")));

        var result =
            await provider.GetCurrentPriceAsync("AAPL");

        Assert.Null(result);
    }

    // TEST 5: Invalid price handling

    [Theory]
    [InlineData("0")]
    [InlineData("-10.50")]
    public async Task GetCurrentPriceAsync_InvalidPrice_ReturnsNull(
        string price)
    {
        string json =
            $$"""
            {
                "c": {{price}},
                "t": 1710000000
            }
            """;

        var provider = CreateProvider(
            (_, _) => Task.FromResult(
                JsonResponse(json)));

        var result =
            await provider.GetCurrentPriceAsync("AAPL");

        Assert.Null(result);
    }

    // TEST 6: Invalid timestamps

    [Theory]
    [InlineData(0L)]
    [InlineData(-100L)]
    [InlineData(long.MaxValue)]
    public async Task GetCurrentPriceAsync_InvalidTimestamp_ReturnsNull(
        long timestamp)
    {
        string json =
            $$"""
            {
                "c": 187.45,
                "t": {{timestamp}}
            }
            """;

        var provider = CreateProvider(
            (_, _) => Task.FromResult(
                JsonResponse(json)));

        var result =
            await provider.GetCurrentPriceAsync("AAPL");

        Assert.Null(result);
    }

    // TEST 7: HTTP errors

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task GetCurrentPriceAsync_HttpError_ThrowsHttpRequestException(
        HttpStatusCode statusCode)
    {
        var provider = CreateProvider(
            (_, _) => Task.FromResult(
                JsonResponse("{}", statusCode)));

        var exception =
            await Assert.ThrowsAsync<HttpRequestException>(
                () => provider.GetCurrentPriceAsync("AAPL"));

        Assert.Equal(statusCode, exception.StatusCode);
    }

    // TEST 8: Invalid JSON

    [Fact]
    public async Task GetCurrentPriceAsync_MalformedJson_ThrowsJsonException()
    {
        var provider = CreateProvider(
            (_, _) => Task.FromResult(
                JsonResponse("{ invalid json }")));

        await Assert.ThrowsAsync<JsonException>(
            () => provider.GetCurrentPriceAsync("AAPL"));
    }

    // TEST 9: Explicit cancellation

    [Fact]
    public async Task GetCurrentPriceAsync_CancelledToken_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();

        cts.Cancel();

        var provider = CreateProvider((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            return Task.FromResult(
                JsonResponse(ValidQuote));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetCurrentPriceAsync(
                "AAPL", cts.Token));
    }

    // TEST 10: Cancellation during an HTTP request

    [Fact]
    public async Task GetCurrentPriceAsync_CancelDuringRequest_StopsRequest()
    {
        using var cts = new CancellationTokenSource();

        var requestStarted =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        var provider = CreateProvider(async (_, ct) =>
        {
            requestStarted.TrySetResult(true);

            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                ct);

            return JsonResponse(ValidQuote);
        });

        Task<MarketPrice?> operation =
            provider.GetCurrentPriceAsync(
                "AAPL", cts.Token);

        await requestStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await operation.WaitAsync(
                TimeSpan.FromSeconds(5)));
    }

    // Fake HTTP handler: no external network required

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            CancellationToken,
            Task<HttpResponseMessage>> responder;

        public FakeHttpMessageHandler(
            Func<HttpRequestMessage,
                 CancellationToken,
                 Task<HttpResponseMessage>> responder)
        {
            this.responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return responder(request, cancellationToken);
        }
    }
}
