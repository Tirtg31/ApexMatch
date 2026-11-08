using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Services;
using Xunit;

namespace TradingEngine.Tests.ConcurrencyTests;

/// <summary>
/// Workflow-level tests against the current in-memory public APIs.
/// These tests assume AccountService, PositionService and OrderMatchingEngine are individually synchronized.
/// They DO NOT prove cross-service atomicity or database-level correctness.
/// Tests marked Skip describe features not yet implemented (cancellation coordination / atomic settlement).
/// Each test owns its service instances so tests do not share state.
/// </summary>
public class TradingWorkflowConcurrencyTests
{
    private const int Instrument = 1;
    private readonly AccountService accounts = new();
    private readonly PositionService positions = new();
    private readonly OrderMatchingEngine engine = new();
    private OrderService Orders => new(accounts, engine, positions);
    private SettlementService Settlements => new(accounts, positions);

    private Account Account(decimal balance, string name = "Trader") => accounts.CreateAccount(new Account { AccountName = name, CashBalance = balance });
    private static Order Request(int accountId, OrderSide side, int qty, decimal price, int instrument = Instrument) => new()
    {
        AccountId = accountId,
        InstrumentId = instrument,
        Side = side,
        OrderType = OrderType.Limit,
        Quantity = qty,
        LimitPrice = price
    };
    private Account Snapshot(int id) => accounts.GetAccountById(id)!;
    private Position? Position(int id, int instrument = Instrument) => positions.GetPosition(id, instrument);

    private static async Task<(int successes, int failures)> Compete(int count, Action action)
    {
        // A start gate increases contention. It does not guarantee an unsafe interleaving.
        using var gate = new ManualResetEventSlim(false);
        int successes = 0, failures = 0;
        var tasks = Enumerable.Range(0, count).Select(_ => Task.Run(() =>
        {
            gate.Wait();
            try { action(); Interlocked.Increment(ref successes); }
            catch (ArgumentException) { Interlocked.Increment(ref failures); }
            catch (InvalidOperationException) { Interlocked.Increment(ref failures); }
        })).ToArray();
        gate.Set();
        await Task.WhenAll(tasks);
        return (successes, failures);
    }

    private (Account buyer, Account seller, Order buy, Order sell, Execution execution) ReadyTrade(
        int qty = 10, decimal buyLimit = 100m, decimal sellLimit = 95m, decimal executionPrice = 100m)
    {
        var buyer = Account(10000m, "Buyer");
        var seller = Account(2000m, "Seller");
        positions.AddPosition(seller.AccountId, Instrument, qty + 10, 80m);
        var buy = Orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, qty, buyLimit));
        var sell = Orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, qty, sellLimit));
        var exec = Assert.Single(engine.LimitOrderMatcher(Instrument));
        Assert.Equal(executionPrice, exec.ExecutedPrice);
        return (buyer, seller, buy, sell, exec);
    }

    [Fact]
    public async Task ConcurrentBuyPlacement_InsufficientCombinedCash_AcceptsExactlyTen()
    {
        var buyer = Account(10000m);
        var (ok, rejected) = await Compete(100, () => Orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, 10, 100m)));
        Assert.Equal(10, ok);
        Assert.Equal(90, rejected);
        var a = Snapshot(buyer.AccountId);
        Assert.Equal(10000m, a.CashBalance);
        Assert.Equal(0m, a.AvailableCash);
        Assert.Equal(10000m, a.ReservedCash);
    }

    [Fact]
    public async Task ConcurrentSellPlacement_InsufficientCombinedShares_AcceptsExactlyTen()
    {
        var seller = Account(0m);
        positions.AddPosition(seller.AccountId, Instrument, 100, 80m);
        var (ok, rejected) = await Compete(100, () => Orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 10, 95m)));
        Assert.Equal(10, ok);
        Assert.Equal(90, rejected);
        Assert.Equal(100, Position(seller.AccountId)!.ReservedQuantity);
        Assert.Equal(0, Position(seller.AccountId)!.AvailableQuantity);
    }

    [Fact]
    public async Task ConcurrentBuyPlacement_OnDifferentAccounts_PreservesIndependentBalances()
    {
        var buyers = Enumerable.Range(0, 30).Select(i => Account(1000m, $"Buyer{i}")).ToArray();
        await Task.WhenAll(buyers.Select(b => Task.Run(() => Orders.PlaceBuyOrder(Request(b.AccountId, OrderSide.Buy, 5, 100m)))));
        foreach (var b in buyers)
        {
            Assert.Equal(500m, Snapshot(b.AccountId).ReservedCash);
            Assert.Equal(500m, Snapshot(b.AccountId).AvailableCash);
        }
    }

    [Fact]
    public async Task ConcurrentSellPlacement_AcrossInstruments_ReservesIndependently()
    {
        var seller = Account(0m);
        positions.AddPosition(seller.AccountId, 1, 40, 10m);
        positions.AddPosition(seller.AccountId, 2, 60, 20m);
        await Task.WhenAll(
            Task.Run(() => Orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 30, 11m, 1))),
            Task.Run(() => Orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 50, 21m, 2))));
        Assert.Equal(30, Position(seller.AccountId, 1)!.ReservedQuantity);
        Assert.Equal(50, Position(seller.AccountId, 2)!.ReservedQuantity);
    }

    [Fact]
    public async Task ConcurrentBuyPlacement_UniqueOrderIdsAndNoLostReservations()
    {
        var buyer = Account(200000m);
        var tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(() => Orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, 1, 100m)))).ToArray();
        var placed = await Task.WhenAll(tasks);
        Assert.Equal(100, placed.Select(o => o.OrderId).Distinct().Count());
        Assert.All(placed, o => Assert.NotNull(engine.GetOrderById(o.OrderId)));
        Assert.Equal(10000m, Snapshot(buyer.AccountId).ReservedCash);
    }

    [Fact]
    public async Task ConcurrentBuyAndSellPlacement_MatchesAtMostAvailableQuantity()
    {
        var buyer = Account(10000m, "Buyer");
        var seller = Account(0m, "Seller");
        positions.AddPosition(seller.AccountId, Instrument, 100, 70m);
        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
            i % 2 == 0
                ? Orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, 10, 100m))
                : Orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 10, 90m)))).ToArray();
        await Task.WhenAll(tasks);
        var executions = engine.LimitOrderMatcher(Instrument);
        Assert.Equal(50, executions.Sum(e => e.ExecutedQuantity));
        Assert.Equal(executions.Count, executions.Select(e => e.ExecutionId).Distinct().Count());
        Assert.Empty(engine.LimitOrderMatcher(Instrument));
    }

    [Fact]
    public async Task ParallelMatcherInvocations_DoNotDoubleFillOrders()
    {
        var buyer = Account(10000m);
        var seller = Account(0m);
        positions.AddPosition(seller.AccountId, Instrument, 100, 50m);
        var buy = Orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, 100, 100m));
        var sell = Orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 100, 90m));
        var groups = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => engine.LimitOrderMatcher(Instrument))));
        Assert.Equal(100, groups.SelectMany(g => g).Sum(e => e.ExecutedQuantity));
        Assert.Equal(100, engine.GetOrderById(buy.OrderId)!.FilledQuantity);
        Assert.Equal(100, engine.GetOrderById(sell.OrderId)!.FilledQuantity);
    }

    [Fact]
    public void FullWorkflow_MatchAndSettle_ConservesCashAndTransfersShares()
    {
        var t = ReadyTrade();
        decimal initialCombinedCash = Snapshot(t.buyer.AccountId).CashBalance + Snapshot(t.seller.AccountId).CashBalance;
        Settlements.SettleExecution(t.execution, t.buy, t.sell);
        var b = Snapshot(t.buyer.AccountId);
        var s = Snapshot(t.seller.AccountId);
        Assert.Equal(initialCombinedCash, b.CashBalance + s.CashBalance);
        Assert.Equal(9000m, b.CashBalance);
        Assert.Equal(3000m, s.CashBalance);
        Assert.Equal(0m, b.ReservedCash);
        Assert.Equal(0, Position(t.seller.AccountId)!.ReservedQuantity);
        Assert.Equal(10, Position(t.buyer.AccountId)!.Quantity);
        Assert.Equal(10, Position(t.seller.AccountId)!.Quantity);
    }

    [Fact]
    public async Task ConcurrentDuplicateSettlement_ExactlyOneSucceeds()
    {
        var t = ReadyTrade();
        var (ok, rejected) = await Compete(30, () => Settlements.SettleExecution(t.execution, t.buy, t.sell));
        Assert.Equal(1, ok);
        Assert.Equal(29, rejected);
        Assert.Equal(9000m, Snapshot(t.buyer.AccountId).CashBalance);
        Assert.Equal(3000m, Snapshot(t.seller.AccountId).CashBalance);
        Assert.Equal(10, Position(t.buyer.AccountId)!.Quantity);
    }

    [Fact]
    public async Task IndependentConcurrentSettlements_AllSucceedAndConserveCash()
    {
        var seller = Account(1000m, "Seller");
        positions.AddPosition(seller.AccountId, Instrument, 100, 50m);
        var buyers = Enumerable.Range(0, 10).Select(i => Account(1000m, $"Buyer{i}")).ToArray();
        var buyOrders = buyers.Select(b => Orders.PlaceBuyOrder(Request(b.AccountId, OrderSide.Buy, 10, 100m))).ToArray();
        var sellOrders = Enumerable.Range(0, 10).Select(_ => Orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 10, 90m))).ToArray();
        var execs = engine.LimitOrderMatcher(Instrument);
        Assert.Equal(10, execs.Count);
        var buyMap = buyOrders.ToDictionary(x => x.OrderId);
        var sellMap = sellOrders.ToDictionary(x => x.OrderId);
        await Task.WhenAll(execs.Select(e => Task.Run(() => Settlements.SettleExecution(e, buyMap[e.BuyOrderId], sellMap[e.SellOrderId]))));
        Assert.Equal(11000m, buyers.Sum(b => Snapshot(b.AccountId).CashBalance) + Snapshot(seller.AccountId).CashBalance);
        Assert.Equal(100, buyers.Sum(b => Position(b.AccountId)!.Quantity));
        Assert.Null(Position(seller.AccountId));
    }

    [Fact]
    public async Task ConcurrentReadsDuringPlacement_AccountSnapshotsKeepCashInvariant()
    {
        var buyer = Account(100000m);
        var writers = Enumerable.Range(0, 100).Select(_ => Task.Run(() => Orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, 1, 100m))));
        var reader = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                var a = Snapshot(buyer.AccountId);
                Assert.Equal(a.CashBalance, a.AvailableCash + a.ReservedCash);
                Assert.True(a.AvailableCash >= 0 && a.ReservedCash >= 0);
            }
        });
        await Task.WhenAll(writers.Cast<Task>().Append(reader));
    }

    [Fact]
    public async Task ConcurrentReadsDuringSellPlacement_PositionSnapshotsRemainValid()
    {
        var seller = Account(0m);
        positions.AddPosition(seller.AccountId, Instrument, 1000, 10m);
        var writers = Enumerable.Range(0, 100).Select(_ => Task.Run(() => Orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 1, 90m))));
        var reader = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                var p = Position(seller.AccountId)!;
                Assert.InRange(p.ReservedQuantity, 0, p.Quantity);
                Assert.Equal(p.Quantity - p.ReservedQuantity, p.AvailableQuantity);
            }
        });
        await Task.WhenAll(writers.Cast<Task>().Append(reader));
    }

    [Fact]
    public async Task ConcurrentCancelSameOrder_OnlyOneCancellationSucceeds()
    {
        var buyer = Account(10000m);
        var order = Orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, 10, 100m));
        int successes = 0;
        await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(() =>
        {
            if (engine.CancelOrder(order.OrderId)) Interlocked.Increment(ref successes);
        })));
        Assert.Equal(1, successes);
        Assert.Equal(OrderStatus.Cancelled, engine.GetOrderById(order.OrderId)!.Status);
        // Do NOT assert that funds were released here: engine.CancelOrder currently changes status only.
    }

    [Fact]
    public async Task ConcurrentCancelVersusMatch_OrderCannotBeFilledAndCancelled()
    {
        var buyer = Account(10000m);
        var seller = Account(0m);
        positions.AddPosition(seller.AccountId, Instrument, 10, 50m);
        var buy = Orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, 10, 100m));
        Orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 10, 90m));
        var cancelTask = Task.Run(() => engine.CancelOrder(buy.OrderId));
        var matchTask = Task.Run(() => engine.LimitOrderMatcher(Instrument));
        await Task.WhenAll(cancelTask, matchTask);
        var final = engine.GetOrderById(buy.OrderId)!;
        Assert.False(final.Status == OrderStatus.Cancelled && final.FilledQuantity > 0);
        Assert.InRange(final.FilledQuantity, 0, final.Quantity);
    }

    // -------- FUTURE CONTRACT / RED tests --------
    // Skipped because these behaviors are not yet exposed by the uploaded OrderService / SettlementService.
    // Enable when you add an application-level cancellation workflow and a transactional settlement design.

    [Fact(Skip = "Requires OrderService.CancelOrder workflow that releases remaining buyer reservations exactly once")]
    public void CancelPendingBuy_ShouldReleaseReservedCashExactlyOnce()
    {
        var buyer = Account(10000m);
        var o = Orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, 10, 100m));
        Assert.True(engine.CancelOrder(o.OrderId));
        Assert.Equal(0m, Snapshot(buyer.AccountId).ReservedCash); // expected future behavior
        Assert.Equal(10000m, Snapshot(buyer.AccountId).AvailableCash);
    }

    [Fact(Skip = "Requires OrderService.CancelOrder workflow that releases remaining seller reservations exactly once")]
    public void CancelPendingSell_ShouldReleaseReservedSharesExactlyOnce()
    {
        var seller = Account(0m);
        positions.AddPosition(seller.AccountId, Instrument, 10, 50m);
        var o = Orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 10, 90m));
        Assert.True(engine.CancelOrder(o.OrderId));
        Assert.Equal(0, Position(seller.AccountId)!.ReservedQuantity); // expected future behavior
    }

    [Fact(Skip = "Requires coordinated cancellation of partially filled orders and release of only unfilled reservations")]
    public void CancelPartiallyFilledBuy_ShouldReleaseOnlyUnfilledCash()
    {
        var buyer = Account(10000m);
        var seller = Account(0m);
        positions.AddPosition(seller.AccountId, Instrument, 4, 50m);
        var buy = Orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, 10, 100m));
        Orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 4, 90m));
        var exec = Assert.Single(engine.LimitOrderMatcher(Instrument));
        var sell = engine.GetOrderById(exec.SellOrderId)!;
        Settlements.SettleExecution(exec, engine.GetOrderById(buy.OrderId)!, sell);
        Assert.True(engine.CancelOrder(buy.OrderId));
        Assert.Equal(0m, Snapshot(buyer.AccountId).ReservedCash); // expected future behavior
    }

    [Fact(Skip = "Requires atomic settlement (transaction or rollback); current SettlementService can partially commit")]
    public void SettlementFailure_ShouldLeaveAllAccountsAndPositionsUnchanged()
    {
        var t = ReadyTrade();
        // Invalidate the seller's reserved position after matching to force the last settlement step to fail.
        positions.ReleaseReservedQuantity(t.seller.AccountId, Instrument, 10);
        var buyerBefore = Snapshot(t.buyer.AccountId);
        var sellerBefore = Snapshot(t.seller.AccountId);
        Assert.Throws<InvalidOperationException>(() => Settlements.SettleExecution(t.execution, t.buy, t.sell));
        Assert.Equal(buyerBefore.CashBalance, Snapshot(t.buyer.AccountId).CashBalance);
        Assert.Equal(sellerBefore.CashBalance, Snapshot(t.seller.AccountId).CashBalance);
        Assert.Null(Position(t.buyer.AccountId));
    }
}
