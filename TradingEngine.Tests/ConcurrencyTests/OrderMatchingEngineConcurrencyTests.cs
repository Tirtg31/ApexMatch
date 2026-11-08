using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Services;
using Xunit;

namespace TradingEngine.Tests.ConcurrencyTests;

// Tests deliberately exercise one SHARED engine per test.
// Race tests are probabilistic: repeat runs; passing does not prove thread safety.
// Snapshot tests deliberately fail until defensive copying is implemented.
public class OrderMatchingEngineConcurrencyTests
{
    private static Order NewOrder(int instrument, OrderSide side, int quantity = 10,
        decimal price = 100m, int account = 1) => new()
        {
            AccountId = account,
            InstrumentId = instrument,
            Side = side,
            OrderType = OrderType.Limit,
            Quantity = quantity,
            LimitPrice = price
        };

    // Start tasks together, then record all exceptions rather than letting one hide the others.
    private static async Task<(T[] Results, Exception[] Errors)> Race<T>(int count, Func<int, T> action)
    {
        using var gate = new ManualResetEventSlim(false);
        var results = new ConcurrentBag<T>();
        var errors = new ConcurrentBag<Exception>();
        var tasks = Enumerable.Range(0, count).Select(i => Task.Run(() =>
        {
            gate.Wait();
            try { results.Add(action(i)); }
            catch (Exception ex) { errors.Add(ex); }
        })).ToArray();
        gate.Set();
        await Task.WhenAll(tasks);
        return (results.ToArray(), errors.ToArray());
    }

    private static void AssertNoErrors(Exception[] errors) =>
        Assert.True(errors.Length == 0,
            $"Unexpected exceptions ({errors.Length}): {string.Join(" | ", errors.Take(5).Select(e => e.ToString()))}");

    [Fact]
    public async Task ConcurrentBuySubmissions_ShouldHaveUniqueIdsAndAllBeRetrievable()
    {
        var engine = new OrderMatchingEngine();
        var (orders, errors) = await Race(300, _ => engine.AddOrder(NewOrder(1, OrderSide.Buy)));
        AssertNoErrors(errors);
        Assert.Equal(300, orders.Length);
        Assert.Equal(300, orders.Select(o => o.OrderId).Distinct().Count());
        Assert.All(orders, o => Assert.NotNull(engine.GetOrderById(o.OrderId)));
    }

    [Fact]
    public async Task ConcurrentSellSubmissions_ShouldHaveUniqueIds()
    {
        var engine = new OrderMatchingEngine();
        var (orders, errors) = await Race(300, _ => engine.AddOrder(NewOrder(1, OrderSide.Sell)));
        AssertNoErrors(errors);
        Assert.Equal(300, orders.Select(o => o.OrderId).Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentFirstOrders_ShouldCreateBookOnlyOnceAndKeepOrders()
    {
        var engine = new OrderMatchingEngine();
        var (orders, errors) = await Race(100, i => engine.AddOrder(NewOrder(42, i % 2 == 0 ? OrderSide.Buy : OrderSide.Sell, 1, i % 2 == 0 ? 90m : 110m)));
        AssertNoErrors(errors);
        Assert.Equal(100, orders.Length);
        Assert.Equal(100, orders.Select(o => o.OrderId).Distinct().Count());
        Assert.Empty(engine.LimitOrderMatcher(42)); // Prices don't cross
    }

    [Fact]
    public async Task ConcurrentOrdersAcrossInstruments_ShouldNotLoseOrders()
    {
        var engine = new OrderMatchingEngine();
        var (orders, errors) = await Race(300, i => engine.AddOrder(NewOrder(i % 15 + 1, OrderSide.Buy)));
        AssertNoErrors(errors);
        Assert.Equal(300, orders.Length);
        Assert.Equal(300, orders.Select(o => o.OrderId).Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentGetOrderById_WhileAdding_ShouldNotThrow()
    {
        var engine = new OrderMatchingEngine();
        var first = engine.AddOrder(NewOrder(1, OrderSide.Buy));
        var (_, errors) = await Race(200, i =>
        {
            if (i % 2 == 0) return engine.AddOrder(NewOrder(1, OrderSide.Buy)).OrderId;
            return engine.GetOrderById(first.OrderId)?.OrderId ?? -1;
        });
        AssertNoErrors(errors);
        Assert.NotNull(engine.GetOrderById(first.OrderId));
    }

    [Fact]
    public async Task ConcurrentMatchingSameBook_ShouldExecuteEachShareAtMostOnce()
    {
        var engine = new OrderMatchingEngine();
        var buy = engine.AddOrder(NewOrder(1, OrderSide.Buy, 100, 110m));
        var sell = engine.AddOrder(NewOrder(1, OrderSide.Sell, 100, 100m));
        var (batches, errors) = await Race(40, _ => engine.LimitOrderMatcher(1));
        AssertNoErrors(errors);
        var executions = batches.SelectMany(x => x).ToArray();
        Assert.Single(executions);
        Assert.Equal(100, executions.Sum(e => e.ExecutedQuantity));
        Assert.Equal(100, engine.GetOrderById(buy.OrderId)!.FilledQuantity);
        Assert.Equal(100, engine.GetOrderById(sell.OrderId)!.FilledQuantity);
        Assert.Equal(OrderStatus.Filled, engine.GetOrderById(buy.OrderId)!.Status);
        Assert.Equal(OrderStatus.Filled, engine.GetOrderById(sell.OrderId)!.Status);
    }

    [Fact]
    public async Task ConcurrentMatchingDifferentBooks_ShouldGenerateUniqueExecutionIds()
    {
        var engine = new OrderMatchingEngine();
        for (int instrument = 1; instrument <= 50; instrument++)
        {
            engine.AddOrder(NewOrder(instrument, OrderSide.Buy, 10, 110m));
            engine.AddOrder(NewOrder(instrument, OrderSide.Sell, 10, 100m));
        }
        var (batches, errors) = await Race(50, i => engine.LimitOrderMatcher(i + 1));
        AssertNoErrors(errors);
        var executions = batches.SelectMany(x => x).ToArray();
        Assert.Equal(50, executions.Length);
        Assert.Equal(50, executions.Select(e => e.ExecutionId).Distinct().Count());
        Assert.Equal(500, executions.Sum(e => e.ExecutedQuantity));
    }

    [Fact]
    public async Task ConcurrentPartialMatching_ShouldNeverOverfillOrders()
    {
        var engine = new OrderMatchingEngine();
        var buy = engine.AddOrder(NewOrder(1, OrderSide.Buy, 100, 110m));
        var sellers = Enumerable.Range(0, 10).Select(_ => engine.AddOrder(NewOrder(1, OrderSide.Sell, 10, 100m))).ToArray();
        var (batches, errors) = await Race(25, _ => engine.LimitOrderMatcher(1));
        AssertNoErrors(errors);
        var executions = batches.SelectMany(x => x).ToArray();
        Assert.Equal(100, executions.Sum(e => e.ExecutedQuantity));
        Assert.Equal(100, engine.GetOrderById(buy.OrderId)!.FilledQuantity);
        Assert.All(sellers, s => Assert.Equal(10, engine.GetOrderById(s.OrderId)!.FilledQuantity));
        Assert.Equal(executions.Length, executions.Select(e => e.ExecutionId).Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentCancelSameOrder_ShouldSucceedOnlyOnce()
    {
        var engine = new OrderMatchingEngine();
        var order = engine.AddOrder(NewOrder(1, OrderSide.Buy));
        var (outcomes, errors) = await Race(50, _ => engine.CancelOrder(order.OrderId));
        AssertNoErrors(errors);
        Assert.Single(outcomes.Where(x => x));
        Assert.Equal(OrderStatus.Cancelled, engine.GetOrderById(order.OrderId)!.Status);
    }

    [Fact]
    public async Task CancelVersusMatch_ShouldNeverEndBothCancelledAndFilled()
    {
        // Either cancellation wins (0 executed) or matching wins (10 executed).
        for (int attempt = 0; attempt < 30; attempt++)
        {
            var engine = new OrderMatchingEngine();
            var buy = engine.AddOrder(NewOrder(1, OrderSide.Buy, 10, 110m));
            var sell = engine.AddOrder(NewOrder(1, OrderSide.Sell, 10, 100m));
            var (outcomes, errors) = await Race(2, i =>
            {
                if (i == 0) return (object)engine.CancelOrder(buy.OrderId);
                return engine.LimitOrderMatcher(1);
            });
            AssertNoErrors(errors);
            var executions = outcomes.OfType<List<Execution>>().SelectMany(x => x).ToArray();
            int filled = engine.GetOrderById(buy.OrderId)!.FilledQuantity;
            var status = engine.GetOrderById(buy.OrderId)!.Status;
            Assert.InRange(filled, 0, 10);
            Assert.Equal(filled, executions.Sum(e => e.ExecutedQuantity));
            Assert.False(status == OrderStatus.Cancelled && filled == 10);
            if (executions.Length != 0)
                Assert.Equal(OrderStatus.Filled, engine.GetOrderById(sell.OrderId)!.Status);
        }
    }

    [Fact]
    public async Task CancelManyWhileMatching_ShouldNotOverfillAnyOrder()
    {
        var engine = new OrderMatchingEngine();
        var buys = Enumerable.Range(0, 100).Select(_ => engine.AddOrder(NewOrder(1, OrderSide.Buy, 1, 110m))).ToArray();
        var sells = Enumerable.Range(0, 100).Select(_ => engine.AddOrder(NewOrder(1, OrderSide.Sell, 1, 100m))).ToArray();
        var (outcomes, errors) = await Race(60, i =>
        {
            if (i % 3 == 0) return (object)engine.LimitOrderMatcher(1);
            return engine.CancelOrder(buys[i].OrderId);
        });
        AssertNoErrors(errors);
        var executions = outcomes.OfType<List<Execution>>().SelectMany(x => x).ToArray();
        Assert.InRange(executions.Sum(x => x.ExecutedQuantity), 0, 100);
        foreach (var o in buys.Concat(sells))
        {
            var snapshot = engine.GetOrderById(o.OrderId)!;
            Assert.InRange(snapshot.FilledQuantity, 0, snapshot.Quantity);
            Assert.False(snapshot.Status == OrderStatus.Filled && snapshot.FilledQuantity != snapshot.Quantity);
        }
    }

    [Fact]
    public async Task ConcurrentAddAndMatch_ShouldNotLoseOrOverfillOrders()
    {
        var engine = new OrderMatchingEngine();
        var (outcomes, errors) = await Race(150, i =>
        {
            if (i % 3 == 0) return (object)engine.LimitOrderMatcher(1);
            return engine.AddOrder(NewOrder(1, i % 2 == 0 ? OrderSide.Buy : OrderSide.Sell, 1, 100m));
        });
        AssertNoErrors(errors);
        var allExecutions = outcomes.OfType<List<Execution>>().SelectMany(x => x)
            .Concat(engine.LimitOrderMatcher(1)).ToArray();
        var orders = outcomes.OfType<Order>().ToArray();
        Assert.Equal(100, orders.Length);
        Assert.Equal(100, orders.Select(o => o.OrderId).Distinct().Count());
        Assert.Equal(allExecutions.Sum(x => x.ExecutedQuantity) * 2,
            orders.Sum(o => engine.GetOrderById(o.OrderId)!.FilledQuantity));
        Assert.All(orders, o => Assert.InRange(engine.GetOrderById(o.OrderId)!.FilledQuantity, 0, o.Quantity));
    }

    [Fact]
    public async Task ConcurrentCancellationOfNonCrossingOrders_ShouldPreventAnyExecutions()
    {
        var engine = new OrderMatchingEngine();
        var buys = Enumerable.Range(0, 50).Select(_ => engine.AddOrder(NewOrder(1, OrderSide.Buy, 1, 90m))).ToArray();
        engine.AddOrder(NewOrder(1, OrderSide.Sell, 100, 110m));
        var (cancelled, errors) = await Race(50, i => engine.CancelOrder(buys[i].OrderId));
        AssertNoErrors(errors);
        Assert.All(cancelled, wasCancelled => Assert.True(wasCancelled));
        Assert.Empty(engine.LimitOrderMatcher(1));
    }

    [Fact]
    public async Task ManyConcurrentMatchersWithNoCrossedPrices_ShouldReturnNoExecutions()
    {
        var engine = new OrderMatchingEngine();
        engine.AddOrder(NewOrder(1, OrderSide.Buy, 100, 90m));
        engine.AddOrder(NewOrder(1, OrderSide.Sell, 100, 110m));
        var (batches, errors) = await Race(80, _ => engine.LimitOrderMatcher(1));
        AssertNoErrors(errors);
        Assert.All(batches, b => Assert.Empty(b));
    }

    [Fact]
    public void GetOrderById_ShouldReturnDefensiveCopy()
    {
        var engine = new OrderMatchingEngine();
        int id = engine.AddOrder(NewOrder(1, OrderSide.Buy)).OrderId;
        var fetched = engine.GetOrderById(id)!;
        fetched.FilledQuantity = 999;
        fetched.Status = OrderStatus.Cancelled;
        var reread = engine.GetOrderById(id)!;
        Assert.Equal(0, reread.FilledQuantity);
        Assert.Equal(OrderStatus.Pending, reread.Status);
        Assert.NotSame(fetched, reread);
    }

    [Fact]
    public void AddOrder_ShouldNotStoreCallerOwnedMutableOrder()
    {
        var engine = new OrderMatchingEngine();
        var input = NewOrder(1, OrderSide.Buy);
        int id = engine.AddOrder(input).OrderId;
        input.Quantity = 999;
        input.Status = OrderStatus.Cancelled;
        var stored = engine.GetOrderById(id)!;
        Assert.Equal(10, stored.Quantity);
        Assert.Equal(OrderStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task CancellationCleanupDuringMatching_ShouldNotCorruptPriorityQueues()
    {
        var engine = new OrderMatchingEngine();
        var buys = Enumerable.Range(0, 100).Select(_ => engine.AddOrder(NewOrder(1, OrderSide.Buy, 1, 110m))).ToArray();
        var sells = Enumerable.Range(0, 100).Select(_ => engine.AddOrder(NewOrder(1, OrderSide.Sell, 1, 100m))).ToArray();
        var (_, errors) = await Race(100, i =>
        {
            if (i < 50) return (object)engine.CancelOrder(buys[i].OrderId);
            return engine.LimitOrderMatcher(1);
        });
        AssertNoErrors(errors);
        engine.LimitOrderMatcher(1);
        Assert.All(buys.Concat(sells), order =>
        {
            var state = engine.GetOrderById(order.OrderId)!;
            Assert.InRange(state.FilledQuantity, 0, 1);
        });
    }

    [Fact]
    public async Task ConcurrentReadsDuringCancellation_ShouldNotThrowOrReturnUnknownOrders()
    {
        var engine = new OrderMatchingEngine();
        var orders = Enumerable.Range(0, 100).Select(_ => engine.AddOrder(NewOrder(1, OrderSide.Buy))).ToArray();
        var (_, errors) = await Race(200, i =>
        {
            var order = orders[i % orders.Length];
            if (i % 2 == 0) return (object)engine.CancelOrder(order.OrderId);
            return engine.GetOrderById(order.OrderId) ?? throw new Exception("Lost stored order");
        });
        AssertNoErrors(errors);
    }
}
