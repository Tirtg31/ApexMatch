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
/// Integration/concurrency tests for the in-memory TradingCoordinator.
/// Assumes the latest signatures discussed in the project:
/// PlaceBuyOrder(Order), PlaceSellOrder(Order), CancelBuyOrder(int),
/// CancelSellOrder(int), MatchAndSettle(int).
/// All requests share a single coordinator and service graph per test.
/// Deliberately excludes transaction rollback/fault injection (deferred to EF Core).
/// </summary>
public class TradingCoordinatorIntegrationTests
{
    private const int Stock = 1;
    private readonly AccountService accounts = new();
    private readonly PositionService positions = new();
    private readonly OrderMatchingEngine engine = new();
    private readonly TradingCoordinator coordinator;

    public TradingCoordinatorIntegrationTests()
    {
        var orders = new OrderService(accounts, engine, positions);
        var settlement = new SettlementService(accounts, positions);
        coordinator = new TradingCoordinator(settlement, engine, orders);
    }

    private Account Create(decimal cash, string name = "Trader") =>
        accounts.CreateAccount(new Account { AccountName = name, CashBalance = cash });

    private static Order Request(Account account, OrderSide side, int quantity, decimal price,
        int instrument = Stock) => new()
        {
            AccountId = account.AccountId,
            InstrumentId = instrument,
            Side = side,
            OrderType = OrderType.Limit,
            Quantity = quantity,
            LimitPrice = price
        };

    private Account Cash(Account account) => accounts.GetAccountById(account.AccountId)!;
    private Position? Holding(Account account, int instrument = Stock) =>
        positions.GetPosition(account.AccountId, instrument);
    private Order Current(Order order) => engine.GetOrderById(order.OrderId)!;

    // All work starts after a gate opens. Exact same-instruction concurrency isn't guaranteed.
    private static async Task<T[]> Concurrent<T>(IEnumerable<Func<T>> work)
    {
        using var gate = new ManualResetEventSlim(false);
        var tasks = work.Select(operation => Task.Run(() =>
        {
            gate.Wait();
            return operation();
        })).ToArray();
        gate.Set();
        return await Task.WhenAll(tasks);
    }

    // Count only expected business rejections, never silently swallow unexpected exceptions.
    private static async Task<(int accepted, int rejected)> Compete(int count, Action operation)
    {
        var results = await Concurrent(Enumerable.Range(0, count).Select(_ => (Func<bool>)(() =>
        {
            try { operation(); return true; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        })));
        return (results.Count(x => x), results.Count(x => !x));
    }

    [Fact]
    public async Task ConcurrentBuyOrders_ReserveOnlyAvailableCash()
    {
        var buyer = Create(10_000m);
        var result = await Compete(100, () => coordinator.PlaceBuyOrder(Request(buyer, OrderSide.Buy, 10, 100m)));
        Assert.Equal((10, 90), result);
        Assert.Equal(0m, Cash(buyer).AvailableCash);
        Assert.Equal(10_000m, Cash(buyer).ReservedCash);
        Assert.Equal(10_000m, Cash(buyer).CashBalance);
    }

    [Fact]
    public async Task ConcurrentSellOrders_CannotReserveMoreThanOwned()
    {
        var seller = Create(0m);
        positions.AddPosition(seller.AccountId, Stock, 100, 70m);
        var result = await Compete(100, () => coordinator.PlaceSellOrder(Request(seller, OrderSide.Sell, 10, 95m)));
        Assert.Equal((10, 90), result);
        Assert.Equal(100, Holding(seller)!.ReservedQuantity);
        Assert.Equal(0, Holding(seller)!.AvailableQuantity);
    }

    [Fact]
    public async Task ConcurrentIndependentBuyOrders_HaveUniqueIdsAndCorrectCash()
    {
        var buyers = Enumerable.Range(1, 40).Select(i => Create(1_000m, $"Buyer{i}")).ToArray();
        var accepted = await Concurrent(buyers.Select(b => (Func<Order>)(() =>
            coordinator.PlaceBuyOrder(Request(b, OrderSide.Buy, 2, 100m)))));
        Assert.Equal(40, accepted.Select(x => x.OrderId).Distinct().Count());
        foreach (var buyer in buyers)
        {
            Assert.Equal(800m, Cash(buyer).AvailableCash);
            Assert.Equal(200m, Cash(buyer).ReservedCash);
        }
    }

    [Fact]
    public async Task ConcurrentIndependentSellOrders_HaveUniqueIdsAndCorrectReservations()
    {
        var sellers = Enumerable.Range(1, 40).Select(i => Create(0m, $"Seller{i}")).ToArray();
        foreach (var s in sellers) positions.AddPosition(s.AccountId, Stock, 5, 80m);
        var accepted = await Concurrent(sellers.Select(s => (Func<Order>)(() =>
            coordinator.PlaceSellOrder(Request(s, OrderSide.Sell, 3, 95m)))));
        Assert.Equal(40, accepted.Select(x => x.OrderId).Distinct().Count());
        foreach (var seller in sellers)
        {
            Assert.Equal(3, Holding(seller)!.ReservedQuantity);
            Assert.Equal(2, Holding(seller)!.AvailableQuantity);
        }
    }

    [Fact]
    public void MatchAndSettle_FullFill_TransfersCashAndShares()
    {
        var buyer = Create(10_000m, "Buyer");
        var seller = Create(1_000m, "Seller");
        positions.AddPosition(seller.AccountId, Stock, 30, 70m);
        var buy = coordinator.PlaceBuyOrder(Request(buyer, OrderSide.Buy, 10, 100m));
        var sell = coordinator.PlaceSellOrder(Request(seller, OrderSide.Sell, 10, 95m));
        var execution = Assert.Single(coordinator.MatchAndSettle(Stock));
        Assert.Equal(10, execution.ExecutedQuantity);
        // The older buy limit sets the execution price in this engine.
        Assert.Equal(100m, execution.ExecutedPrice);
        Assert.Equal(OrderStatus.Filled, Current(buy).Status);
        Assert.Equal(OrderStatus.Filled, Current(sell).Status);
        Assert.Equal(9_000m, Cash(buyer).CashBalance);
        Assert.Equal(9_000m, Cash(buyer).AvailableCash);
        Assert.Equal(0m, Cash(buyer).ReservedCash);
        Assert.Equal(2_000m, Cash(seller).CashBalance);
        Assert.Equal(10, Holding(buyer)!.Quantity);
        Assert.Equal(20, Holding(seller)!.Quantity);
        Assert.Equal(0, Holding(seller)!.ReservedQuantity);
    }

    [Fact]
    public void MatchAndSettle_SellOrderOlder_PriceImprovementReleasesCashDifference()
    {
        var seller = Create(0m);
        var buyer = Create(2_000m);
        positions.AddPosition(seller.AccountId, Stock, 10, 60m);
        coordinator.PlaceSellOrder(Request(seller, OrderSide.Sell, 5, 90m));
        coordinator.PlaceBuyOrder(Request(buyer, OrderSide.Buy, 5, 100m));
        var e = Assert.Single(coordinator.MatchAndSettle(Stock));
        Assert.Equal(90m, e.ExecutedPrice);
        Assert.Equal(1_550m, Cash(buyer).CashBalance);
        Assert.Equal(1_550m, Cash(buyer).AvailableCash);
        Assert.Equal(0m, Cash(buyer).ReservedCash);
        Assert.Equal(450m, Cash(seller).CashBalance);
    }

    [Fact]
    public void MatchAndSettle_NoCross_DoesNotChangeReservations()
    {
        var buyer = Create(1_000m);
        var seller = Create(0m);
        positions.AddPosition(seller.AccountId, Stock, 10, 80m);
        var buy = coordinator.PlaceBuyOrder(Request(buyer, OrderSide.Buy, 5, 90m));
        var sell = coordinator.PlaceSellOrder(Request(seller, OrderSide.Sell, 5, 95m));
        Assert.Empty(coordinator.MatchAndSettle(Stock));
        Assert.Equal(450m, Cash(buyer).ReservedCash);
        Assert.Equal(5, Holding(seller)!.ReservedQuantity);
        Assert.Equal(OrderStatus.Pending, Current(buy).Status);
        Assert.Equal(OrderStatus.Pending, Current(sell).Status);
    }

    [Fact]
    public void MatchAndSettle_PartialFillThenCancelBuy_ReleasesOnlyUnfilledReservation()
    {
        var buyer = Create(2_000m);
        var seller = Create(0m);
        positions.AddPosition(seller.AccountId, Stock, 10, 80m);
        var buy = coordinator.PlaceBuyOrder(Request(buyer, OrderSide.Buy, 10, 100m));
        coordinator.PlaceSellOrder(Request(seller, OrderSide.Sell, 4, 95m));
        Assert.Equal(4, Assert.Single(coordinator.MatchAndSettle(Stock)).ExecutedQuantity);
        Assert.Equal(OrderStatus.PartiallyFilled, Current(buy).Status);
        Assert.True(coordinator.CancelBuyOrder(buy.OrderId));
        Assert.Equal(OrderStatus.Cancelled, Current(buy).Status);
        Assert.Equal(1_600m, Cash(buyer).CashBalance);
        Assert.Equal(1_600m, Cash(buyer).AvailableCash);
        Assert.Equal(0m, Cash(buyer).ReservedCash);
        Assert.Equal(4, Holding(buyer)!.Quantity);
    }

    [Fact]
    public void MatchAndSettle_PartialFillThenCancelSell_ReleasesOnlyUnfilledShares()
    {
        var buyer = Create(2_000m);
        var seller = Create(0m);
        positions.AddPosition(seller.AccountId, Stock, 20, 70m);
        var sell = coordinator.PlaceSellOrder(Request(seller, OrderSide.Sell, 10, 95m));
        coordinator.PlaceBuyOrder(Request(buyer, OrderSide.Buy, 4, 100m));
        Assert.Equal(4, Assert.Single(coordinator.MatchAndSettle(Stock)).ExecutedQuantity);
        Assert.True(coordinator.CancelSellOrder(sell.OrderId));
        Assert.Equal(16, Holding(seller)!.Quantity);
        Assert.Equal(0, Holding(seller)!.ReservedQuantity);
        Assert.Equal(OrderStatus.Cancelled, Current(sell).Status);
    }

    [Fact]
    public void CancelPendingBuy_ReleasesReservationExactlyOnce()
    {
        var buyer = Create(1_000m);
        var buy = coordinator.PlaceBuyOrder(Request(buyer, OrderSide.Buy, 5, 100m));
        Assert.True(coordinator.CancelBuyOrder(buy.OrderId));
        Assert.False(coordinator.CancelBuyOrder(buy.OrderId));
        Assert.Equal(1_000m, Cash(buyer).AvailableCash);
        Assert.Equal(0m, Cash(buyer).ReservedCash);
        Assert.Empty(coordinator.MatchAndSettle(Stock));
    }

    [Fact]
    public void CancelPendingSell_ReleasesSharesExactlyOnce()
    {
        var seller = Create(0m);
        positions.AddPosition(seller.AccountId, Stock, 10, 70m);
        var sell = coordinator.PlaceSellOrder(Request(seller, OrderSide.Sell, 6, 100m));
        Assert.True(coordinator.CancelSellOrder(sell.OrderId));
        Assert.False(coordinator.CancelSellOrder(sell.OrderId));
        Assert.Equal(0, Holding(seller)!.ReservedQuantity);
        Assert.Equal(10, Holding(seller)!.AvailableQuantity);
    }

    [Fact]
    public async Task ManyConcurrentCancelsOfOneBuy_OnlyOneReleasesCash()
    {
        var buyer = Create(1_000m);
        var buy = coordinator.PlaceBuyOrder(Request(buyer, OrderSide.Buy, 5, 100m));
        var outcomes = await Concurrent(Enumerable.Range(0, 30)
            .Select(_ => (Func<bool>)(() => coordinator.CancelBuyOrder(buy.OrderId))));
        Assert.Single(outcomes.Where(x => x));
        Assert.Equal(1_000m, Cash(buyer).AvailableCash);
        Assert.Equal(0m, Cash(buyer).ReservedCash);
    }

    [Fact]
    public async Task ManyConcurrentCancelsOfOneSell_OnlyOneReleasesShares()
    {
        var seller = Create(0m);
        positions.AddPosition(seller.AccountId, Stock, 15, 80m);
        var sell = coordinator.PlaceSellOrder(Request(seller, OrderSide.Sell, 10, 90m));
        var outcomes = await Concurrent(Enumerable.Range(0, 30)
            .Select(_ => (Func<bool>)(() => coordinator.CancelSellOrder(sell.OrderId))));
        Assert.Single(outcomes.Where(x => x));
        Assert.Equal(0, Holding(seller)!.ReservedQuantity);
        Assert.Equal(15, Holding(seller)!.Quantity);
    }

    [Fact]
    public async Task MatchVersusCancelBuy_EitherWins_WithoutDoubleReleaseOrFill()
    {
        var buyer = Create(1_000m);
        var seller = Create(0m);
        positions.AddPosition(seller.AccountId, Stock, 10, 80m);
        var buy = coordinator.PlaceBuyOrder(Request(buyer, OrderSide.Buy, 5, 100m));
        coordinator.PlaceSellOrder(Request(seller, OrderSide.Sell, 5, 95m));
        var outcomes = await Concurrent(new Func<(bool cancelled, List<Execution> executions)>[]
        {
            () => (coordinator.CancelBuyOrder(buy.OrderId), new List<Execution>()),
            () => (false, coordinator.MatchAndSettle(Stock))
        });
        var cancelled = outcomes[0].cancelled;
        var executions = outcomes[1].executions;
        Assert.Equal(cancelled ? 0 : 1, executions.Count);
        Assert.Equal(cancelled ? OrderStatus.Cancelled : OrderStatus.Filled, Current(buy).Status);
        Assert.Equal(cancelled ? 1_000m : 500m, Cash(buyer).AvailableCash);
        Assert.Equal(0m, Cash(buyer).ReservedCash);
        Assert.Equal(cancelled ? 0 : 5, Holding(buyer)?.Quantity ?? 0);
    }

    [Fact]
    public async Task MatchVersusCancelSell_EitherWins_WithoutOverselling()
    {
        var buyer = Create(1_000m);
        var seller = Create(0m);
        positions.AddPosition(seller.AccountId, Stock, 10, 80m);
        coordinator.PlaceBuyOrder(Request(buyer, OrderSide.Buy, 5, 100m));
        var sell = coordinator.PlaceSellOrder(Request(seller, OrderSide.Sell, 5, 95m));
        var outcomes = await Concurrent(new Func<(bool cancelled, List<Execution> executions)>[]
        {
            () => (coordinator.CancelSellOrder(sell.OrderId), new List<Execution>()),
            () => (false, coordinator.MatchAndSettle(Stock))
        });
        var cancelled = outcomes[0].cancelled;
        Assert.Equal(cancelled ? 0 : 1, outcomes[1].executions.Count);
        Assert.Equal(cancelled ? 10 : 5, Holding(seller)!.Quantity);
        Assert.Equal(0, Holding(seller)!.ReservedQuantity);
        Assert.Equal(cancelled ? 0m : 500m, Cash(seller).CashBalance);
    }

    [Fact]
    public async Task ConcurrentMatchingCalls_ExecuteTheSameLiquidityOnlyOnce()
    {
        var buyer = Create(2_000m);
        var seller = Create(0m);
        positions.AddPosition(seller.AccountId, Stock, 10, 80m);
        var buy = coordinator.PlaceBuyOrder(Request(buyer, OrderSide.Buy, 10, 100m));
        coordinator.PlaceSellOrder(Request(seller, OrderSide.Sell, 10, 95m));
        var batches = await Concurrent(Enumerable.Range(0, 30)
            .Select(_ => (Func<List<Execution>>)(() => coordinator.MatchAndSettle(Stock))));
        var executions = batches.SelectMany(x => x).ToArray();
        Assert.Single(executions);
        Assert.Equal(10, Current(buy).FilledQuantity);
        Assert.Equal(1_000m, Cash(buyer).CashBalance);
        Assert.Equal(1_000m, Cash(seller).CashBalance);
        Assert.Equal(10, Holding(buyer)!.Quantity);
        Assert.Null(Holding(seller));
    }

    [Fact]
    public void MatchAndSettle_MultipleExecutionsForOneOrder_SettlesAllExactlyOnce()
    {
        var buyer = Create(5_000m);
        var sellers = Enumerable.Range(0, 3).Select(i => Create(0m, $"Seller{i}")).ToArray();
        var buy = coordinator.PlaceBuyOrder(Request(buyer, OrderSide.Buy, 12, 100m));
        foreach (var seller in sellers)
        {
            positions.AddPosition(seller.AccountId, Stock, 4, 60m);
            coordinator.PlaceSellOrder(Request(seller, OrderSide.Sell, 4, 90m));
        }
        var executions = coordinator.MatchAndSettle(Stock);
        Assert.Equal(3, executions.Count);
        Assert.Equal(12, executions.Sum(e => e.ExecutedQuantity));
        Assert.Equal(3, executions.Select(e => e.ExecutionId).Distinct().Count());
        Assert.Equal(12, Current(buy).FilledQuantity);
        Assert.Equal(12, Holding(buyer)!.Quantity);
        Assert.Equal(3_800m, Cash(buyer).CashBalance);
        Assert.Equal(0m, Cash(buyer).ReservedCash);
        foreach (var seller in sellers) Assert.Equal(400m, Cash(seller).CashBalance);
        Assert.Empty(coordinator.MatchAndSettle(Stock));
    }

    [Fact]
    public void MultipleBuyersCompeteForOneSeller_NoOverfill()
    {
        var seller = Create(0m);
        var buyers = Enumerable.Range(0, 3).Select(i => Create(2_000m, $"Buyer{i}")).ToArray();
        positions.AddPosition(seller.AccountId, Stock, 10, 75m);
        var sell = coordinator.PlaceSellOrder(Request(seller, OrderSide.Sell, 10, 90m));
        foreach (var buyer in buyers) coordinator.PlaceBuyOrder(Request(buyer, OrderSide.Buy, 6, 100m));
        var executions = coordinator.MatchAndSettle(Stock);
        Assert.Equal(10, executions.Sum(e => e.ExecutedQuantity));
        Assert.Equal(10, Current(sell).FilledQuantity);
        Assert.Null(Holding(seller));
        Assert.Equal(900m, Cash(seller).CashBalance);
        Assert.Equal(10, buyers.Sum(b => Holding(b)?.Quantity ?? 0));
        Assert.Equal(8, buyers.Sum(b => (int)(Cash(b).ReservedCash / 100m)));
    }

    [Fact]
    public void MultipleSellersCompeteForOneBuyer_NoOverfill()
    {
        var buyer = Create(2_000m);
        var sellers = Enumerable.Range(0, 3).Select(i => Create(0m, $"Seller{i}")).ToArray();
        var buy = coordinator.PlaceBuyOrder(Request(buyer, OrderSide.Buy, 10, 100m));
        foreach (var seller in sellers)
        {
            positions.AddPosition(seller.AccountId, Stock, 6, 70m);
            coordinator.PlaceSellOrder(Request(seller, OrderSide.Sell, 6, 90m));
        }
        var executions = coordinator.MatchAndSettle(Stock);
        Assert.Equal(10, executions.Sum(e => e.ExecutedQuantity));
        Assert.Equal(10, Current(buy).FilledQuantity);
        Assert.Equal(10, Holding(buyer)!.Quantity);
        Assert.Equal(0m, Cash(buyer).ReservedCash);
        Assert.Equal(1_000m, Cash(buyer).CashBalance);
        Assert.Equal(10, sellers.Sum(s => 6 - (Holding(s)?.Quantity ?? 0)));
    }

    [Fact]
    public async Task SimultaneousMatchingDifferentInstruments_KeepsAssetsSeparate()
    {
        var buyer = Create(10_000m);
        var seller = Create(0m);
        positions.AddPosition(seller.AccountId, 1, 5, 80m);
        positions.AddPosition(seller.AccountId, 2, 7, 60m);
        coordinator.PlaceBuyOrder(Request(buyer, OrderSide.Buy, 5, 100m, 1));
        coordinator.PlaceSellOrder(Request(seller, OrderSide.Sell, 5, 90m, 1));
        coordinator.PlaceBuyOrder(Request(buyer, OrderSide.Buy, 7, 50m, 2));
        coordinator.PlaceSellOrder(Request(seller, OrderSide.Sell, 7, 45m, 2));
        var batches = await Concurrent(new Func<List<Execution>>[]
        {
            () => coordinator.MatchAndSettle(1),
            () => coordinator.MatchAndSettle(2)
        });
        Assert.Equal(12, batches.Sum(x => x.Sum(e => e.ExecutedQuantity)));
        Assert.Equal(5, Holding(buyer, 1)!.Quantity);
        Assert.Equal(7, Holding(buyer, 2)!.Quantity);
        Assert.Equal(9_150m, Cash(buyer).CashBalance);
        Assert.Equal(850m, Cash(seller).CashBalance);
        Assert.Null(Holding(seller, 1));
        Assert.Null(Holding(seller, 2));
    }

    [Fact]
    public async Task HighVolumePlacementThenMatching_ConservesCashAndShares()
    {
        const int n = 40;
        var buyers = Enumerable.Range(0, n).Select(i => Create(500m, $"Buyer{i}")).ToArray();
        var sellers = Enumerable.Range(0, n).Select(i => Create(0m, $"Seller{i}")).ToArray();
        foreach (var seller in sellers) positions.AddPosition(seller.AccountId, Stock, 1, 70m);
        var buyerOrders = await Concurrent(buyers.Select(b => (Func<Order>)(() =>
            coordinator.PlaceBuyOrder(Request(b, OrderSide.Buy, 1, 100m)))));
        var sellerOrders = await Concurrent(sellers.Select(s => (Func<Order>)(() =>
            coordinator.PlaceSellOrder(Request(s, OrderSide.Sell, 1, 100m)))));
        Assert.Equal(2 * n, buyerOrders.Concat(sellerOrders).Select(o => o.OrderId).Distinct().Count());
        var batches = await Concurrent(Enumerable.Range(0, 8)
            .Select(_ => (Func<List<Execution>>)(() => coordinator.MatchAndSettle(Stock))));
        var executions = batches.SelectMany(x => x).ToArray();
        Assert.Equal(n, executions.Sum(e => e.ExecutedQuantity));
        Assert.Equal(n, executions.Select(e => e.ExecutionId).Distinct().Count());
        Assert.Equal(20_000m, buyers.Sum(b => Cash(b).CashBalance) + sellers.Sum(s => Cash(s).CashBalance));
        Assert.Equal(n, buyers.Sum(b => Holding(b)?.Quantity ?? 0));
        Assert.All(buyers, b => { Assert.Equal(0m, Cash(b).ReservedCash); Assert.Equal(400m, Cash(b).CashBalance); });
        Assert.All(sellers, s => Assert.Null(Holding(s)));
    }

    [Fact]
    public void FilledOrder_CannotBeCancelledOrReleasedAgain()
    {
        var buyer = Create(1_000m);
        var seller = Create(0m);
        positions.AddPosition(seller.AccountId, Stock, 5, 70m);
        var buy = coordinator.PlaceBuyOrder(Request(buyer, OrderSide.Buy, 5, 100m));
        var sell = coordinator.PlaceSellOrder(Request(seller, OrderSide.Sell, 5, 100m));
        Assert.Single(coordinator.MatchAndSettle(Stock));
        Assert.False(coordinator.CancelBuyOrder(buy.OrderId));
        Assert.False(coordinator.CancelSellOrder(sell.OrderId));
        Assert.Equal(500m, Cash(buyer).CashBalance);
        Assert.Equal(0m, Cash(buyer).ReservedCash);
        Assert.Equal(500m, Cash(seller).CashBalance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MatchAndSettle_InvalidInstrument_Throws(int instrumentId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.MatchAndSettle(instrumentId));
    }

    [Fact]
    public void CancellationWrongSide_IsRejectedAndDoesNotReleaseFunds()
    {
        var buyer = Create(1_000m);
        var buy = coordinator.PlaceBuyOrder(Request(buyer, OrderSide.Buy, 4, 100m));
        Assert.Throws<InvalidOperationException>(() => coordinator.CancelSellOrder(buy.OrderId));
        Assert.Equal(400m, Cash(buyer).ReservedCash);
        Assert.Equal(OrderStatus.Pending, Current(buy).Status);
    }
}
