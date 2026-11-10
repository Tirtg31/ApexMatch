
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Interfaces.Repositories.MarketData;
using TradingEngine.Infrastructure.MarketData;
using Xunit;

namespace TradingEngine.Tests.MarketData;

public sealed class MarketPriceStoreTests
{
    private static MarketPrice CreatePrice(
        string symbol = "AAPL",
        decimal price = 187.45m,
        DateTimeOffset? timestamp = null)
    {
        return new MarketPrice
        {
            InstrumentSymbol = symbol,
            Price = price,
            LastUpdatedUtc = timestamp ?? DateTimeOffset.UtcNow,
            Provider = "Finnhub"
        };
    }

    // TEST 1: Adding a new price

    [Fact]
    public void UpdatePrice_NewInstrument_StoresPrice()
    {
        var store = new MarketPriceStore();
        var marketPrice = CreatePrice();

        store.UpdatePrice(marketPrice);

        bool found = store.TryGetPrice("AAPL", out var result);

        Assert.True(found);
        Assert.NotNull(result);
        Assert.Equal(187.45m, result.Price);
        Assert.Equal("AAPL", result.InstrumentSymbol);
        Assert.Equal("Finnhub", result.Provider);
        Assert.Equal(marketPrice.LastUpdatedUtc, result.LastUpdatedUtc);
    }

    // TEST 2: Retrieving a missing instrument

    [Fact]
    public void TryGetPrice_UnknownInstrument_ReturnsFalse()
    {
        var store = new MarketPriceStore();

        bool found = store.TryGetPrice("MSFT", out var result);

        Assert.False(found);
        Assert.Null(result);
    }

    // TEST 3: Updating an existing instrument

    [Fact]
    public void UpdatePrice_ExistingInstrument_ReplacesPrice()
    {
        var store = new MarketPriceStore();

        store.UpdatePrice(CreatePrice("AAPL", 187.45m));
        store.UpdatePrice(CreatePrice("AAPL", 190.50m));

        bool found = store.TryGetPrice("AAPL", out var result);

        Assert.True(found);
        Assert.NotNull(result);
        Assert.Equal(190.50m, result.Price);
        Assert.Single(store.GetAllPrices());
    }

    // TEST 4: Case-insensitive lookup

    [Theory]
    [InlineData("AAPL")]
    [InlineData("aapl")]
    [InlineData("AaPl")]
    public void TryGetPrice_DifferentLetterCase_ReturnsPrice(
        string symbol)
    {
        var store = new MarketPriceStore();

        store.UpdatePrice(CreatePrice("AAPL"));

        bool found = store.TryGetPrice(symbol, out var result);

        Assert.True(found);
        Assert.NotNull(result);
        Assert.Equal("AAPL", result.InstrumentSymbol);
    }

    // TEST 5: Trimming and normalizing during update

    [Fact]
    public void UpdatePrice_SymbolWithWhitespace_NormalizesSymbol()
    {
        var store = new MarketPriceStore();

        store.UpdatePrice(CreatePrice(" aapl "));

        bool found = store.TryGetPrice("AAPL", out var result);

        Assert.True(found);
        Assert.NotNull(result);
        Assert.Equal("AAPL", result.InstrumentSymbol);
        Assert.Single(store.GetAllPrices());
    }

    // TEST 6: Trimming and normalizing during lookup

    [Fact]
    public void TryGetPrice_SymbolWithWhitespace_FindsPrice()
    {
        var store = new MarketPriceStore();

        store.UpdatePrice(CreatePrice("AAPL"));

        bool found = store.TryGetPrice(" aapl ", out var result);

        Assert.True(found);
        Assert.NotNull(result);
        Assert.Equal("AAPL", result.InstrumentSymbol);
    }

    // TEST 7: Invalid lookup symbols

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    public void TryGetPrice_InvalidSymbol_ReturnsFalse(
        string? symbol)
    {
        var store = new MarketPriceStore();

        bool found = store.TryGetPrice(symbol!, out var result);

        Assert.False(found);
        Assert.Null(result);
    }

    // TEST 8: Null MarketPrice

    [Fact]
    public void UpdatePrice_NullPrice_ThrowsArgumentNullException()
    {
        var store = new MarketPriceStore();

        Assert.Throws<ArgumentNullException>(
            () => store.UpdatePrice(null!));

        Assert.Empty(store.GetAllPrices());
    }

    // TEST 9: Invalid instrument symbols

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    public void UpdatePrice_InvalidSymbol_ThrowsArgumentException(
        string? symbol)
    {
        var store = new MarketPriceStore();

        var price = CreatePrice(symbol!);

        Assert.Throws<ArgumentException>(
            () => store.UpdatePrice(price));

        Assert.Empty(store.GetAllPrices());
    }

    // TEST 10: Zero or negative prices

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-150)]
    public void UpdatePrice_NonPositivePrice_ThrowsException(
        int invalidPrice)
    {
        var store = new MarketPriceStore();

        var price = CreatePrice(
            "AAPL",
            invalidPrice);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => store.UpdatePrice(price));

        Assert.Empty(store.GetAllPrices());
    }

    // TEST 11: Normalization does not modify original record

    [Fact]
    public void UpdatePrice_Normalization_DoesNotMutateOriginal()
    {
        var store = new MarketPriceStore();

        var original = CreatePrice(" aapl ");

        store.UpdatePrice(original);

        store.TryGetPrice("AAPL", out var stored);

        Assert.NotNull(stored);

        Assert.Equal(" aapl ", original.InstrumentSymbol);
        Assert.Equal("AAPL", stored.InstrumentSymbol);

        Assert.NotSame(original, stored);
    }

    // TEST 12: GetAllPrices returns all instruments

    [Fact]
    public void GetAllPrices_MultipleInstruments_ReturnsAll()
    {
        var store = new MarketPriceStore();

        store.UpdatePrice(CreatePrice("AAPL", 187m));
        store.UpdatePrice(CreatePrice("MSFT", 420m));
        store.UpdatePrice(CreatePrice("TSLA", 250m));

        var prices = store.GetAllPrices();

        Assert.Equal(3, prices.Count);

        Assert.Contains(
            prices,
            p => p.InstrumentSymbol == "AAPL");

        Assert.Contains(
            prices,
            p => p.InstrumentSymbol == "MSFT");

        Assert.Contains(
            prices,
            p => p.InstrumentSymbol == "TSLA");
    }

    // TEST 13: Snapshot remains unchanged after update

    [Fact]
    public void GetAllPrices_UpdateAfterSnapshot_DoesNotChangeSnapshot()
    {
        var store = new MarketPriceStore();

        store.UpdatePrice(CreatePrice("AAPL", 187m));

        var snapshot = store.GetAllPrices();

        store.UpdatePrice(CreatePrice("AAPL", 195m));
        store.UpdatePrice(CreatePrice("MSFT", 420m));

        Assert.Single(snapshot);
        Assert.Equal(187m, snapshot.Single().Price);

        var latest = store.GetAllPrices();

        Assert.Equal(2, latest.Count);

        Assert.Contains(
            latest,
            p => p.InstrumentSymbol == "AAPL"
                 && p.Price == 195m);

        Assert.Contains(
            latest,
            p => p.InstrumentSymbol == "MSFT"
                 && p.Price == 420m);
    }

    // TEST 14: Independent stores must not share data

    [Fact]
    public void MarketPriceStore_DifferentInstances_AreIndependent()
    {
        var store1 = new MarketPriceStore();
        var store2 = new MarketPriceStore();

        store1.UpdatePrice(CreatePrice("AAPL", 187m));

        Assert.True(store1.TryGetPrice("AAPL", out _));
        Assert.False(store2.TryGetPrice("AAPL", out _));

        Assert.Single(store1.GetAllPrices());
        Assert.Empty(store2.GetAllPrices());
    }

    // TEST 15: Concurrent writes to different instruments

    [Fact]
    public void UpdatePrice_ConcurrentDifferentInstruments_StoresAll()
    {
        var store = new MarketPriceStore();

        const int instrumentCount = 1000;

        Parallel.For(0, instrumentCount, i =>
        {
            store.UpdatePrice(
                CreatePrice(
                    $"STOCK{i}",
                    100m + i));
        });

        var allPrices = store.GetAllPrices();

        Assert.Equal(instrumentCount, allPrices.Count);

        for (int i = 0; i < instrumentCount; i++)
        {
            bool found = store.TryGetPrice(
                $"STOCK{i}",
                out var price);

            Assert.True(found);
            Assert.NotNull(price);
            Assert.Equal(100m + i, price.Price);
        }
    }

    // TEST 16: Concurrent updates to the same instrument

    [Fact]
    public void UpdatePrice_ConcurrentSameInstrument_KeepsSingleEntry()
    {
        var store = new MarketPriceStore();

        const int updateCount = 1000;

        Parallel.For(1, updateCount + 1, i =>
        {
            store.UpdatePrice(
                CreatePrice("AAPL", i));
        });

        var allPrices = store.GetAllPrices();

        Assert.Single(allPrices);

        bool found = store.TryGetPrice("AAPL", out var result);

        Assert.True(found);
        Assert.NotNull(result);
        Assert.InRange(result.Price, 1m, 1000m);
    }

    // TEST 17: Concurrent reads and writes

    [Fact]
    public async Task MarketPriceStore_ConcurrentReadsAndWrites_NoExceptions()
    {
        var store = new MarketPriceStore();

        store.UpdatePrice(CreatePrice("AAPL", 100m));

        var errors = new ConcurrentBag<Exception>();

        Task writer = Task.Run(() =>
        {
            for (int i = 1; i <= 1000; i++)
            {
                try
                {
                    store.UpdatePrice(
                        CreatePrice("AAPL", 100m + i));
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }
        });

        Task reader = Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                try
                {
                    bool found = store.TryGetPrice(
                        "AAPL",
                        out var price);

                    if (!found || price is null)
                    {
                        throw new InvalidOperationException(
                            "Previously cached AAPL price was lost.");
                    }

                    if (price.Price <= 0)
                    {
                        throw new InvalidOperationException(
                            "Invalid market price observed.");
                    }

                    _ = store.GetAllPrices();
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }
        });

        await Task.WhenAll(writer, reader);

        Assert.Empty(errors);

        Assert.True(store.TryGetPrice("AAPL", out var result));
        Assert.NotNull(result);
        Assert.Equal(1100m, result.Price);
        Assert.Single(store.GetAllPrices());
    }

    // TEST 18: Different case variants under concurrency

    [Fact]
    public void UpdatePrice_ConcurrentCaseVariants_StoresSingleEntry()
    {
        var store = new MarketPriceStore();

        string[] variants =
        {
            "AAPL",
            "aapl",
            "AaPl",
            " aapl ",
            " AAPL "
        };

        Parallel.For(0, 1000, i =>
        {
            store.UpdatePrice(
                CreatePrice(
                    variants[i % variants.Length],
                    100m + i));
        });

        var prices = store.GetAllPrices();

        Assert.Single(prices);
        Assert.Equal("AAPL", prices.Single().InstrumentSymbol);
    }
}
