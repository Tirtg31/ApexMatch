using System;
using System.Collections.Generic;
using System.Linq;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Services;
using Xunit;

namespace TradingEngine.Tests;

/// <summary>
/// Pure in-memory recovery tests: no Oracle database required.
/// These tests assume ValidateOrder checks the order's intrinsic validity,
/// while live duplicate-ID checks happen ONLY in RegisterPersistedOrder.
/// </summary>
public class OrderMatchingEngineRecoveryTests
{
    private static readonly DateTime BaseTime =
        new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Order NewOrder(
        int orderId,
        OrderSide side = OrderSide.Buy,
        int instrumentId = 10,
        int quantity = 100,
        decimal price = 100m,
        int filledQuantity = 0,
        OrderStatus status = OrderStatus.Pending,
        int timeOffsetSeconds = 0)
    {
        return new Order
        {
            OrderId = orderId,
            AccountId = side == OrderSide.Buy ? 1 : 2,
            InstrumentId = instrumentId,
            Side = side,
            OrderType = OrderType.Limit,
            Quantity = quantity,
            LimitPrice = price,
            FilledQuantity = filledQuantity,
            Status = status,
            CreatedAt = BaseTime.AddSeconds(timeOffsetSeconds)
        };
    }

    [Fact]
    public void Rebuild_RestoresMultipleOrders()
    {
        var engine = new OrderMatchingEngine();
        Order buy = NewOrder(101, OrderSide.Buy);
        Order sell = NewOrder(102, OrderSide.Sell);

        engine.RebuildFromPersistedOrders(new[] { buy, sell });

        Order? restoredBuy = engine.GetOrderById(101);
        Order? restoredSell = engine.GetOrderById(102);
        Assert.NotNull(restoredBuy);
        Assert.NotNull(restoredSell);
        Assert.Equal(101, restoredBuy.OrderId);
        Assert.Equal(OrderSide.Buy, restoredBuy.Side);
        Assert.Equal(102, restoredSell.OrderId);
        Assert.Equal(OrderSide.Sell, restoredSell.Side);

        // Recovery stores clones; modifying source objects cannot alter the engine.
        buy.Quantity = 999;
        Assert.Equal(100, engine.GetOrderById(101)!.Quantity);
    }

    [Fact]
    public void Rebuild_PreservesPartiallyFilledOrders()
    {
        var engine = new OrderMatchingEngine();
        Order partial = NewOrder(201, quantity: 100, filledQuantity: 40,
            status: OrderStatus.PartiallyFilled);

        engine.RebuildFromPersistedOrders(new[] { partial });

        Order? restored = engine.GetOrderById(201);
        Assert.NotNull(restored);
        Assert.Equal(100, restored.Quantity);
        Assert.Equal(40, restored.FilledQuantity);
        Assert.Equal(60, restored.Quantity - restored.FilledQuantity);
        Assert.Equal(OrderStatus.PartiallyFilled, restored.Status);
    }

    [Fact]
    public void Rebuild_InvalidOrder_DoesNotModifyExistingState()
    {
        var engine = new OrderMatchingEngine();
        engine.RegisterPersistedOrder(NewOrder(300));
        Order good = NewOrder(301);
        Order invalid = NewOrder(302, quantity: 0);

        Assert.Throws<ArgumentException>(() =>
            engine.RebuildFromPersistedOrders(new[] { good, invalid }));

        Assert.NotNull(engine.GetOrderById(300));
        Assert.Equal(OrderStatus.Pending, engine.GetOrderById(300)!.Status);
        Assert.Null(engine.GetOrderById(301));
        Assert.Null(engine.GetOrderById(302));
    }

    [Fact]
    public void Rebuild_CalledTwice_DoesNotCreateDuplicates()
    {
        var engine = new OrderMatchingEngine();
        Order[] snapshot =
        {
            NewOrder(401, OrderSide.Buy),
            NewOrder(402, OrderSide.Sell, timeOffsetSeconds: 1)
        };

        engine.RebuildFromPersistedOrders(snapshot);
        engine.RebuildFromPersistedOrders(snapshot);

        Assert.NotNull(engine.GetOrderById(401));
        Assert.NotNull(engine.GetOrderById(402));

        // Running destructive matching only on this isolated test instance
        // confirms rebuilding did not leave duplicate executable entries.
        var executions = engine.LimitOrderMatcher(10);
        Assert.Single(executions);
        Assert.Equal(100, executions[0].ExecutedQuantity);
        Assert.Equal(OrderStatus.Filled, engine.GetOrderById(401)!.Status);
        Assert.Equal(OrderStatus.Filled, engine.GetOrderById(402)!.Status);
    }

    [Fact]
    public void Rebuild_MultipleInstruments_CreatesSeparateBooks()
    {
        var engine = new OrderMatchingEngine();
        Order buy10 = NewOrder(501, OrderSide.Buy, instrumentId: 10);
        Order sell10 = NewOrder(502, OrderSide.Sell, instrumentId: 10);
        Order buy20 = NewOrder(503, OrderSide.Buy, instrumentId: 20);

        engine.RebuildFromPersistedOrders(new[] { buy10, sell10, buy20 });

        Assert.Equal(10, engine.GetOrderById(501)!.InstrumentId);
        Assert.Equal(10, engine.GetOrderById(502)!.InstrumentId);
        Assert.Equal(20, engine.GetOrderById(503)!.InstrumentId);

        // These calls mutate this isolated test engine. They prove that
        // instrument 20's BUY cannot match instrument 10's SELL.
        Assert.Empty(engine.LimitOrderMatcher(20));
        var executions = engine.LimitOrderMatcher(10);
        Assert.Single(executions);
        Assert.Equal(501, executions[0].BuyOrderId);
        Assert.Equal(502, executions[0].SellOrderId);
    }

    [Fact]
    public void Rebuild_DuplicateOrderId_ThrowsWithoutPublishing()
    {
        var engine = new OrderMatchingEngine();
        engine.RegisterPersistedOrder(NewOrder(600));
        Order first = NewOrder(601);
        Order duplicate = NewOrder(601, OrderSide.Sell);

        Assert.Throws<InvalidOperationException>(() =>
            engine.RebuildFromPersistedOrders(new[] { first, duplicate }));

        Assert.NotNull(engine.GetOrderById(600));
        Assert.Null(engine.GetOrderById(601));
    }

    [Fact]
    public void Rebuild_EmptyList_ClearsExistingState()
    {
        var engine = new OrderMatchingEngine();
        engine.RegisterPersistedOrder(NewOrder(701));
        engine.RegisterPersistedOrder(NewOrder(702, OrderSide.Sell));

        engine.RebuildFromPersistedOrders(Array.Empty<Order>());

        Assert.Null(engine.GetOrderById(701));
        Assert.Null(engine.GetOrderById(702));
        Assert.Empty(engine.LimitOrderMatcher(10));
    }

    [Fact]
    public void Rebuild_PreservesPriceTimePriority()
    {
        var engine = new OrderMatchingEngine();
        // Input deliberately shuffled. Oldest buy at highest price must match first.
        Order laterBuy = NewOrder(802, OrderSide.Buy, quantity: 10,
            price: 105m, timeOffsetSeconds: 20);
        Order sell = NewOrder(803, OrderSide.Sell, quantity: 10,
            price: 90m, timeOffsetSeconds: 30);
        Order earlierBuy = NewOrder(801, OrderSide.Buy, quantity: 10,
            price: 105m, timeOffsetSeconds: 10);
        Order lowerBid = NewOrder(804, OrderSide.Buy, quantity: 10,
            price: 100m, timeOffsetSeconds: 0);

        engine.RebuildFromPersistedOrders(
            new[] { laterBuy, sell, lowerBid, earlierBuy });

        // LimitOrderMatcher is destructive. Use it only on this isolated
        // test engine; do not call it for inspection in production recovery.
        List<Execution> executions = engine.LimitOrderMatcher(10);

        Assert.Single(executions);
        Assert.Equal(801, executions[0].BuyOrderId);
        Assert.Equal(803, executions[0].SellOrderId);
        Assert.Equal(10, executions[0].ExecutedQuantity);
        Assert.Equal(OrderStatus.Filled, engine.GetOrderById(801)!.Status);
        Assert.Equal(OrderStatus.Pending, engine.GetOrderById(802)!.Status);
        Assert.Equal(OrderStatus.Pending, engine.GetOrderById(804)!.Status);
    }
}
