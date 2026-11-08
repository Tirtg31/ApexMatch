using System;
using System.Linq;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Services;
using Xunit;

namespace TradingEngine.Tests;

// Supplementary tests: the existing OrderCancellationTests and
// OrderMatchingEngineEdgeCaseTests cover the basic cases already.
public class TradingEnginePriorityCancellationRegressionTests
{
    private const int Instrument = 1;
    private readonly OrderMatchingEngine engine = new();

    private static Order NewOrder(OrderSide side, int qty, decimal price) => new()
    {
        AccountId = 1,
        InstrumentId = Instrument,
        Side = side,
        OrderType = OrderType.Limit,
        Quantity = qty,
        LimitPrice = price
    };

    private Order Add(OrderSide side, int qty, decimal price) =>
        engine.AddOrder(NewOrder(side, qty, price));

    private Order Stored(Order order) => engine.GetOrderById(order.OrderId)!;

    // TEST 01: FIFO on the ask side even with three equal-price sellers.
    [Fact]
    public void SamePriceSells_ExecuteOldestFirst()
    {
        var first = Add(OrderSide.Sell, 10, 100m);
        var second = Add(OrderSide.Sell, 10, 100m);
        var third = Add(OrderSide.Sell, 10, 100m);
        var buy = Add(OrderSide.Buy, 20, 100m);

        var fills = engine.LimitOrderMatcher(Instrument);

        Assert.Equal(2, fills.Count);
        Assert.Equal(new[] { first.OrderId, second.OrderId }, fills.Select(e => e.SellOrderId));
        Assert.Equal(OrderStatus.Filled, Stored(first).Status);
        Assert.Equal(OrderStatus.Filled, Stored(second).Status);
        Assert.Equal(OrderStatus.Pending, Stored(third).Status);
        Assert.Equal(OrderStatus.Filled, Stored(buy).Status);
    }

    // TEST 02: Price beats arrival time for BUY orders.
    [Fact]
    public void HigherBid_BeatsEarlierLowerBid()
    {
        var older = Add(OrderSide.Buy, 10, 100m);
        var newer = Add(OrderSide.Buy, 10, 105m);
        var sell = Add(OrderSide.Sell, 10, 99m);

        var fill = Assert.Single(engine.LimitOrderMatcher(Instrument));

        Assert.Equal(newer.OrderId, fill.BuyOrderId);
        Assert.Equal(sell.OrderId, fill.SellOrderId);
        Assert.Equal(0, Stored(older).FilledQuantity);
    }

    // TEST 03: Price beats arrival time for SELL orders.
    [Fact]
    public void LowerAsk_BeatsEarlierHigherAsk()
    {
        var older = Add(OrderSide.Sell, 10, 100m);
        var newer = Add(OrderSide.Sell, 10, 95m);
        var buy = Add(OrderSide.Buy, 10, 105m);

        var fill = Assert.Single(engine.LimitOrderMatcher(Instrument));

        Assert.Equal(newer.OrderId, fill.SellOrderId);
        Assert.Equal(buy.OrderId, fill.BuyOrderId);
        Assert.Equal(0, Stored(older).FilledQuantity);
    }

    // TEST 04: A partially filled order keeps its original queue position.
    [Fact]
    public void PartialFill_DoesNotLosePriorityToNewerSamePriceBid()
    {
        var first = Add(OrderSide.Buy, 100, 100m);
        var second = Add(OrderSide.Buy, 40, 100m);
        Add(OrderSide.Sell, 30, 100m);
        Assert.Equal(first.OrderId, Assert.Single(engine.LimitOrderMatcher(Instrument)).BuyOrderId);

        Add(OrderSide.Sell, 80, 100m);
        var fills = engine.LimitOrderMatcher(Instrument);

        Assert.Equal(2, fills.Count);
        Assert.Equal(new[] { first.OrderId, second.OrderId }, fills.Select(e => e.BuyOrderId));
        Assert.Equal(new[] { 70, 10 }, fills.Select(e => e.ExecutedQuantity));
        Assert.Equal(OrderStatus.Filled, Stored(first).Status);
        Assert.Equal(10, Stored(second).FilledQuantity);
    }

    // TEST 05: Cancelled top bid cannot block the next eligible bid.
    [Fact]
    public void CancelledHighestBid_IsSkipped()
    {
        var top = Add(OrderSide.Buy, 10, 110m);
        var next = Add(OrderSide.Buy, 10, 105m);
        Assert.True(engine.CancelOrder(top.OrderId));
        Add(OrderSide.Sell, 10, 100m);

        var fill = Assert.Single(engine.LimitOrderMatcher(Instrument));

        Assert.Equal(next.OrderId, fill.BuyOrderId);
        Assert.Equal(OrderStatus.Cancelled, Stored(top).Status);
    }

    // TEST 06: Cancelled top ask cannot block the next eligible ask.
    [Fact]
    public void CancelledLowestAsk_IsSkipped()
    {
        var top = Add(OrderSide.Sell, 10, 90m);
        var next = Add(OrderSide.Sell, 10, 95m);
        Assert.True(engine.CancelOrder(top.OrderId));
        Add(OrderSide.Buy, 10, 100m);

        var fill = Assert.Single(engine.LimitOrderMatcher(Instrument));

        Assert.Equal(next.OrderId, fill.SellOrderId);
        Assert.Equal(OrderStatus.Cancelled, Stored(top).Status);
    }

    // TEST 07: A cancelled middle order must not execute after the first fills.
    [Fact]
    public void CancelledMiddleBid_IsSkippedWithoutReorderingOthers()
    {
        var first = Add(OrderSide.Buy, 10, 100m);
        var middle = Add(OrderSide.Buy, 10, 100m);
        var last = Add(OrderSide.Buy, 10, 100m);
        Assert.True(engine.CancelOrder(middle.OrderId));
        Add(OrderSide.Sell, 20, 100m);

        var fills = engine.LimitOrderMatcher(Instrument);

        Assert.Equal(new[] { first.OrderId, last.OrderId }, fills.Select(e => e.BuyOrderId));
        Assert.Equal(0, Stored(middle).FilledQuantity);
        Assert.Equal(OrderStatus.Cancelled, Stored(middle).Status);
    }

    // TEST 08: Earlier BUY is maker, therefore the BUY price is used.
    [Fact]
    public void EarlierBuy_SetsExecutionPrice()
    {
        Add(OrderSide.Buy, 10, 105m);
        Add(OrderSide.Sell, 10, 95m);
        Assert.Equal(105m, Assert.Single(engine.LimitOrderMatcher(Instrument)).ExecutedPrice);
    }

    // TEST 09: Earlier SELL is maker, therefore the SELL price is used.
    [Fact]
    public void EarlierSell_SetsExecutionPrice()
    {
        Add(OrderSide.Sell, 10, 95m);
        Add(OrderSide.Buy, 10, 105m);
        Assert.Equal(95m, Assert.Single(engine.LimitOrderMatcher(Instrument)).ExecutedPrice);
    }

    // TEST 10: External modifications to a returned snapshot must not affect the book.
    [Fact]
    public void MutatingReturnedSnapshot_DoesNotMutateStoredOrder()
    {
        var order = Add(OrderSide.Buy, 10, 100m);
        var snapshot = engine.GetOrderById(order.OrderId)!;
        snapshot.Quantity = 1_000;
        snapshot.LimitPrice = 1m;
        snapshot.Status = OrderStatus.Cancelled;
        snapshot.FilledQuantity = 10;

        var stored = Stored(order);
        Assert.Equal(10, stored.Quantity);
        Assert.Equal(100m, stored.LimitPrice);
        Assert.Equal(0, stored.FilledQuantity);
        Assert.Equal(OrderStatus.Pending, stored.Status);
    }

    // TEST 11: Already executed BUY quantity survives cancellation, remainder cannot execute.
    [Fact]
    public void CancelPartiallyFilledBuy_PreservesFillAndPreventsFutureFill()
    {
        var buy = Add(OrderSide.Buy, 100, 100m);
        Add(OrderSide.Sell, 40, 100m);
        Assert.Single(engine.LimitOrderMatcher(Instrument));
        Assert.True(engine.CancelOrder(buy.OrderId));
        Add(OrderSide.Sell, 60, 100m);

        Assert.Empty(engine.LimitOrderMatcher(Instrument));
        Assert.Equal(40, Stored(buy).FilledQuantity);
        Assert.Equal(OrderStatus.Cancelled, Stored(buy).Status);
    }

    // TEST 12: Already executed SELL quantity survives cancellation, remainder cannot execute.
    [Fact]
    public void CancelPartiallyFilledSell_PreservesFillAndPreventsFutureFill()
    {
        var sell = Add(OrderSide.Sell, 100, 100m);
        Add(OrderSide.Buy, 40, 100m);
        Assert.Single(engine.LimitOrderMatcher(Instrument));
        Assert.True(engine.CancelOrder(sell.OrderId));
        Add(OrderSide.Buy, 60, 100m);

        Assert.Empty(engine.LimitOrderMatcher(Instrument));
        Assert.Equal(40, Stored(sell).FilledQuantity);
        Assert.Equal(OrderStatus.Cancelled, Stored(sell).Status);
    }
}
