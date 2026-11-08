using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Services;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradingEngine.Tests.Persistence;

// Requires a disposable Oracle TEST schema with all migrations applied.
// Each fixture uses unique accounts/instrument; verification uses fresh DbContexts.
public sealed class TradingCoordinatorMatchingIntegrationTests
{
    private static TradingDbContext CreateDb()
    {
        var connection = "User Id=trading;Password=trade_123;Data Source=localhost:1521/XEPDB1";
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseOracle(connection, o => o.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion21))
            .Options;
        return new TradingDbContext(options);
    }

    private static TradingCoordinator CreateCoordinator(TradingDbContext db, OrderMatchingEngine engine)
    {
        var uow = new EfUnitOfWork(db, NullLogger<EfUnitOfWork>.Instance);
        var accounts = new AccountRepository(db);
        var positions = new PositionRepository(db);
        var orders = new OrderRepository(db);
        var executions = new ExecutionRepository(db);
        var accountService = new AccountService(accounts, uow);
        var positionService = new PositionService(positions, uow);
        var settlement = new SettlementService(accountService, positionService, uow, orders, executions);
        var orderService = new OrderService(accountService, engine, positionService, accounts, uow, orders);
        return new TradingCoordinator(settlement, engine, orderService, orders);
    }

    private sealed record Fixture(int BuyerId, int SellerId, int InstrumentId);

    private static async Task<Fixture> SeedAsync(decimal buyerCash = 20000m, int sellerShares = 100)
    {
        await using var db = CreateDb();
        var buyer = new Account
        {
            AccountName = $"MatchBuyer_{Guid.NewGuid():N}",
            CashBalance = buyerCash,
            AvailableCash = buyerCash,
            ReservedCash = 0m,
            IsActive = true
        };
        var seller = new Account
        {
            AccountName = $"MatchSeller_{Guid.NewGuid():N}",
            CashBalance = 200m,
            AvailableCash = 200m,
            ReservedCash = 0m,
            IsActive = true
        };
        var instrument = new TradeInstrument
        {
            InstrumentName = $"Match_{Guid.NewGuid():N}",
            InstrumentSymbol = $"T{Guid.NewGuid():N}"[..20],
            IsActive = true
        };
        db.Accounts.AddRange(buyer, seller);
        db.TradeInstruments.Add(instrument);
        await db.SaveChangesAsync();
        db.Positions.Add(new Position
        {
            AccountId = seller.AccountId,
            InstrumentId = instrument.InstrumentId,
            Quantity = sellerShares,
            ReservedQuantity = 0,
            AverageCost = 70m
        });
        await db.SaveChangesAsync();
        return new Fixture(buyer.AccountId, seller.AccountId, instrument.InstrumentId);
    }

    private static Order OrderFor(Fixture f, OrderSide side, int quantity, decimal price) => new()
    {
        AccountId = side == OrderSide.Buy ? f.BuyerId : f.SellerId,
        InstrumentId = f.InstrumentId,
        Side = side,
        OrderType = OrderType.Limit,
        Quantity = quantity,
        LimitPrice = price
    };

    private static async Task CleanupAsync(Fixture f)
    {
        await using var db = CreateDb();
        var orderIds = await db.Orders.Where(o => o.InstrumentId == f.InstrumentId)
            .Select(o => o.OrderId).ToListAsync();
        await db.Executions.Where(e => orderIds.Contains(e.BuyOrderId) || orderIds.Contains(e.SellOrderId))
            .ExecuteDeleteAsync();
        await db.Orders.Where(o => o.InstrumentId == f.InstrumentId).ExecuteDeleteAsync();
        await db.Positions.Where(p => p.InstrumentId == f.InstrumentId).ExecuteDeleteAsync();
        await db.Accounts.Where(a => a.AccountId == f.BuyerId || a.AccountId == f.SellerId).ExecuteDeleteAsync();
        await db.TradeInstruments.Where(i => i.InstrumentId == f.InstrumentId).ExecuteDeleteAsync();
    }

    private static async Task<List<Execution>> PersistedExecutionsAsync(TradingDbContext db, Fixture f) =>
        await db.Executions.AsNoTracking().Where(e => db.Orders.Any(o => o.InstrumentId == f.InstrumentId &&
            (o.OrderId == e.BuyOrderId || o.OrderId == e.SellOrderId))).ToListAsync();

    [Fact]
    public async Task MatchAndSettle_FullFill_PersistsCashSharesOrdersAndExecution()
    {
        var f = await SeedAsync();
        try
        {
            await using var db = CreateDb();
            var engine = new OrderMatchingEngine();
            var coordinator = CreateCoordinator(db, engine);
            var sell = await coordinator.PlaceSellOrderAsync(OrderFor(f, OrderSide.Sell, 10, 100m));
            var buy = await coordinator.PlaceBuyOrderAsync(OrderFor(f, OrderSide.Buy, 10, 105m));
            var result = await coordinator.MatchAndSettleAsync(f.InstrumentId);
            var execution = Assert.Single(result);
            Assert.Equal(10, execution.ExecutedQuantity);
            Assert.Equal(100m, execution.ExecutedPrice); // older SELL sets price
            Assert.Null(engine.ProposeMatch(f.InstrumentId));
            Assert.Equal(OrderStatus.Filled, engine.GetOrderById(buy.OrderId)!.Status);
            Assert.Equal(OrderStatus.Filled, engine.GetOrderById(sell.OrderId)!.Status);

            await using var verify = CreateDb();
            var buyer = await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.BuyerId);
            var seller = await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.SellerId);
            var bp = await verify.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.BuyerId && p.InstrumentId == f.InstrumentId);
            var sp = await verify.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.SellerId && p.InstrumentId == f.InstrumentId);
            Assert.Equal(19000m, buyer.CashBalance);
            Assert.Equal(19000m, buyer.AvailableCash);
            Assert.Equal(0m, buyer.ReservedCash);
            Assert.Equal(1200m, seller.CashBalance);
            Assert.Equal(10, bp.Quantity);
            Assert.Equal(100m, bp.AverageCost);
            Assert.Equal(90, sp.Quantity);
            Assert.Equal(0, sp.ReservedQuantity);
            Assert.Equal(OrderStatus.Filled, (await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == buy.OrderId)).Status);
            Assert.Equal(OrderStatus.Filled, (await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == sell.OrderId)).Status);
            Assert.Single(await PersistedExecutionsAsync(verify, f));
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task MatchAndSettle_PartialBuy_KeepsRemainingOrderAndReservation()
    {
        var f = await SeedAsync();
        try
        {
            await using var db = CreateDb();
            var engine = new OrderMatchingEngine();
            var coordinator = CreateCoordinator(db, engine);
            var buy = await coordinator.PlaceBuyOrderAsync(OrderFor(f, OrderSide.Buy, 20, 105m));
            var sell = await coordinator.PlaceSellOrderAsync(OrderFor(f, OrderSide.Sell, 8, 100m));
            Assert.Single(await coordinator.MatchAndSettleAsync(f.InstrumentId));
            await using var verify = CreateDb();
            var b = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == buy.OrderId);
            var s = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == sell.OrderId);
            var account = await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.BuyerId);
            Assert.Equal(8, b.FilledQuantity);
            Assert.Equal(OrderStatus.PartiallyFilled, b.Status);
            Assert.Equal(OrderStatus.Filled, s.Status);
            Assert.Equal(12 * 105m, account.ReservedCash);
            Assert.Equal(20_000m - 20 * 105m, account.AvailableCash); // No price improvement: BUY older
            Assert.Equal(OrderStatus.PartiallyFilled, engine.GetOrderById(buy.OrderId)!.Status);
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task MatchAndSettle_PartialSell_KeepsRemainingReservedShares()
    {
        var f = await SeedAsync();
        try
        {
            await using var db = CreateDb();
            var engine = new OrderMatchingEngine();
            var coordinator = CreateCoordinator(db, engine);
            var sell = await coordinator.PlaceSellOrderAsync(OrderFor(f, OrderSide.Sell, 15, 100m));
            var buy = await coordinator.PlaceBuyOrderAsync(OrderFor(f, OrderSide.Buy, 6, 105m));
            Assert.Single(await coordinator.MatchAndSettleAsync(f.InstrumentId));
            await using var verify = CreateDb();
            var order = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == sell.OrderId);
            var shares = await verify.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.SellerId);
            Assert.Equal(6, order.FilledQuantity);
            Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
            Assert.Equal(94, shares.Quantity);
            Assert.Equal(9, shares.ReservedQuantity);
            Assert.Equal(OrderStatus.Filled, engine.GetOrderById(buy.OrderId)!.Status);
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task MatchAndSettle_TwoSells_FillsOneBuyInTwoExecutions()
    {
        var f = await SeedAsync();
        try
        {
            await using var db = CreateDb();
            var engine = new OrderMatchingEngine();
            var coordinator = CreateCoordinator(db, engine);
            var buy = await coordinator.PlaceBuyOrderAsync(OrderFor(f, OrderSide.Buy, 10, 105m));
            var sell1 = await coordinator.PlaceSellOrderAsync(OrderFor(f, OrderSide.Sell, 4, 100m));
            var sell2 = await coordinator.PlaceSellOrderAsync(OrderFor(f, OrderSide.Sell, 6, 101m));
            var result = await coordinator.MatchAndSettleAsync(f.InstrumentId);
            Assert.Equal(2, result.Count);
            Assert.Equal(10, result.Sum(e => e.ExecutedQuantity));
            Assert.Null(engine.ProposeMatch(f.InstrumentId));
            await using var verify = CreateDb();
            Assert.Equal(2, (await PersistedExecutionsAsync(verify, f)).Count);
            Assert.Equal(OrderStatus.Filled, (await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == buy.OrderId)).Status);
            Assert.Equal(OrderStatus.Filled, (await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == sell1.OrderId)).Status);
            Assert.Equal(OrderStatus.Filled, (await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == sell2.OrderId)).Status);
            Assert.Equal(0m, (await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.BuyerId)).ReservedCash);
        }
        finally { await CleanupAsync(f); }
    }

    [Theory]
    [InlineData(99, 100)] // non-crossing
    [InlineData(100, 100)] // equal prices do cross; tested separately below
    public async Task MatchAndSettle_PriceScenarios(int buyPrice, int sellPrice)
    {
        var f = await SeedAsync();
        try
        {
            await using var db = CreateDb();
            var engine = new OrderMatchingEngine();
            var coordinator = CreateCoordinator(db, engine);
            var buy = await coordinator.PlaceBuyOrderAsync(OrderFor(f, OrderSide.Buy, 10, buyPrice));
            var sell = await coordinator.PlaceSellOrderAsync(OrderFor(f, OrderSide.Sell, 10, sellPrice));
            var result = await coordinator.MatchAndSettleAsync(f.InstrumentId);
            await using var verify = CreateDb();
            if (buyPrice < sellPrice)
            {
                Assert.Empty(result);
                Assert.Equal(OrderStatus.Pending, engine.GetOrderById(buy.OrderId)!.Status);
                Assert.Equal(OrderStatus.Pending, engine.GetOrderById(sell.OrderId)!.Status);
                Assert.Empty(await PersistedExecutionsAsync(verify, f));
            }
            else
            {
                Assert.Single(result);
                Assert.Equal((decimal)buyPrice, result[0].ExecutedPrice);
                Assert.Single(await PersistedExecutionsAsync(verify, f));
            }
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task MatchAndSettle_SecondCall_DoesNotDuplicateExecution()
    {
        var f = await SeedAsync();
        try
        {
            await using var db = CreateDb();
            var engine = new OrderMatchingEngine();
            var coordinator = CreateCoordinator(db, engine);
            await coordinator.PlaceBuyOrderAsync(OrderFor(f, OrderSide.Buy, 10, 105m));
            await coordinator.PlaceSellOrderAsync(OrderFor(f, OrderSide.Sell, 10, 100m));
            Assert.Single(await coordinator.MatchAndSettleAsync(f.InstrumentId));
            Assert.Empty(await coordinator.MatchAndSettleAsync(f.InstrumentId));
            await using var verify = CreateDb();
            Assert.Single(await PersistedExecutionsAsync(verify, f));
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task MatchAndSettle_AfterRecovery_DoesNotReplayFilledOrders()
    {
        var f = await SeedAsync();
        try
        {
            await using var db = CreateDb();
            var engine = new OrderMatchingEngine();
            var coordinator = CreateCoordinator(db, engine);
            await coordinator.PlaceBuyOrderAsync(OrderFor(f, OrderSide.Buy, 10, 105m));
            await coordinator.PlaceSellOrderAsync(OrderFor(f, OrderSide.Sell, 10, 100m));
            Assert.Single(await coordinator.MatchAndSettleAsync(f.InstrumentId));
            await coordinator.RecoverOrderBookAsync();
            Assert.Empty(await coordinator.MatchAndSettleAsync(f.InstrumentId));
            await using var verify = CreateDb();
            Assert.Single(await PersistedExecutionsAsync(verify, f));
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task MatchAndSettle_AfterDatabaseRejection_SuspendsUntilRecovery()
    {
        var f = await SeedAsync();
        try
        {
            await using var db = CreateDb();
            var engine = new OrderMatchingEngine();
            var coordinator = CreateCoordinator(db, engine);
            var buy = await coordinator.PlaceBuyOrderAsync(OrderFor(f, OrderSide.Buy, 10, 105m));
            await coordinator.PlaceSellOrderAsync(OrderFor(f, OrderSide.Sell, 10, 100m));
            // Simulates out-of-band cancellation without releasing the reservation.
            // DB order is authoritative; matching engine still contains stale order.
            await using (var external = CreateDb())
            {
                var order = await external.Orders.SingleAsync(o => o.OrderId == buy.OrderId);
                order.Status = OrderStatus.Cancelled;
                await external.SaveChangesAsync();
            }
            // Use a NEW context so this test does not reuse tracked stale orders
            // left by placement in the original context.
            await using var matchingDb = CreateDb();
            var matchingCoordinator = CreateCoordinator(matchingDb, engine);
            await Assert.ThrowsAnyAsync<Exception>(() => matchingCoordinator.MatchAndSettleAsync(f.InstrumentId));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                matchingCoordinator.PlaceBuyOrderAsync(OrderFor(f, OrderSide.Buy, 1, 105m)));
            await using (var verify = CreateDb())
                Assert.Empty(await PersistedExecutionsAsync(verify, f));
            // Recovery reloads persisted state, removing the cancelled buy.
            await matchingCoordinator.RecoverOrderBookAsync();
            Assert.Empty(await matchingCoordinator.MatchAndSettleAsync(f.InstrumentId));
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task MatchAndSettle_ConcurrentCallsOnSameCoordinator_SettleOnlyOnce()
    {
        var f = await SeedAsync();
        try
        {
            await using var db = CreateDb();
            var engine = new OrderMatchingEngine();
            var coordinator = CreateCoordinator(db, engine);
            await coordinator.PlaceBuyOrderAsync(OrderFor(f, OrderSide.Buy, 10, 105m));
            await coordinator.PlaceSellOrderAsync(OrderFor(f, OrderSide.Sell, 10, 100m));
            var tasks = Enumerable.Range(0, 6)
                .Select(_ => coordinator.MatchAndSettleAsync(f.InstrumentId)).ToArray();
            var results = await Task.WhenAll(tasks);
            Assert.Equal(1, results.Sum(x => x.Count));
            await using var verify = CreateDb();
            Assert.Single(await PersistedExecutionsAsync(verify, f));
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task MatchAndSettle_InvalidInstrumentId_DoesNotChangeDatabase()
    {
        var f = await SeedAsync();
        try
        {
            await using var db = CreateDb();
            var coordinator = CreateCoordinator(db, new OrderMatchingEngine());
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => coordinator.MatchAndSettleAsync(0));
            await using var verify = CreateDb();
            Assert.Empty(await PersistedExecutionsAsync(verify, f));
        }
        finally { await CleanupAsync(f); }
    }
}
