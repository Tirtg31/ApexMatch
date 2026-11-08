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
public sealed class TradingEngineEndToEndIntegrationTests
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


    // TEST 1: Entire lifecycle across a simulated process restart.
    // Validates order placement, reservations, partial settlement, recovery,
    // second settlement, durable records, and no replay after another restart.
    [Fact]
    public async Task EndToEnd_TwoFillsAcrossRestart_PersistsBalancesAndNeverReplays()
    {
        var f = await SeedAsync(buyerCash: 20000m, sellerShares: 100);
        try
        {
            int buyId, sellOneId, sellTwoId;

            // First process: buy arrives before sell, so BUY limit is the execution price.
            await using (var db = CreateDb())
            {
                var engine = new OrderMatchingEngine();
                var coordinator = CreateCoordinator(db, engine);
                var buy = await coordinator.PlaceBuyOrderAsync(OrderFor(f, OrderSide.Buy, 10, 105m));
                buyId = buy.OrderId;

                await using (var verify = CreateDb())
                {
                    var persistedBuyer = await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.BuyerId);
                    Assert.Equal(1050m, persistedBuyer.ReservedCash);
                    Assert.Equal(18950m, persistedBuyer.AvailableCash);
                    Assert.Equal(20000m, persistedBuyer.CashBalance);
                }

                var sellOne = await coordinator.PlaceSellOrderAsync(OrderFor(f, OrderSide.Sell, 4, 100m));
                sellOneId = sellOne.OrderId;
                var first = Assert.Single(await coordinator.MatchAndSettleAsync(f.InstrumentId));
                Assert.Equal(4, first.ExecutedQuantity);
                Assert.Equal(105m, first.ExecutedPrice);
                Assert.Equal(4, engine.GetOrderById(buyId)!.FilledQuantity);
                Assert.Equal(OrderStatus.PartiallyFilled, engine.GetOrderById(buyId)!.Status);

                await using var persisted = CreateDb();
                var buyer = await persisted.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.BuyerId);
                var sellerPosition = await persisted.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.SellerId && p.InstrumentId == f.InstrumentId);
                Assert.Equal(630m, buyer.ReservedCash); // 6 * 105 still reserved.
                Assert.Equal(18950m, buyer.AvailableCash);
                Assert.Equal(19580m, buyer.CashBalance);
                Assert.Equal(96, sellerPosition.Quantity);
                Assert.Equal(0, sellerPosition.ReservedQuantity);
            }

            // Second process: new engine + coordinator; load partially filled buy from Oracle.
            await using (var db = CreateDb())
            {
                var engine = new OrderMatchingEngine();
                var coordinator = CreateCoordinator(db, engine);
                await coordinator.RecoverOrderBookAsync();
                var restored = engine.GetOrderById(buyId);
                Assert.NotNull(restored);
                Assert.Equal(4, restored.FilledQuantity);
                Assert.Equal(OrderStatus.PartiallyFilled, restored.Status);

                var sellTwo = await coordinator.PlaceSellOrderAsync(OrderFor(f, OrderSide.Sell, 6, 101m));
                sellTwoId = sellTwo.OrderId;
                var second = Assert.Single(await coordinator.MatchAndSettleAsync(f.InstrumentId));
                Assert.Equal(6, second.ExecutedQuantity);
                Assert.Equal(105m, second.ExecutedPrice);
                Assert.Equal(OrderStatus.Filled, engine.GetOrderById(buyId)!.Status);
                Assert.Empty(await coordinator.MatchAndSettleAsync(f.InstrumentId));
            }

            // All assertions below query a FRESH EF DbContext, not cached tracked entities.
            await using (var verify = CreateDb())
            {
                var buyer = await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.BuyerId);
                var seller = await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.SellerId);
                var buyerPosition = await verify.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.BuyerId && p.InstrumentId == f.InstrumentId);
                var sellerPosition = await verify.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.SellerId && p.InstrumentId == f.InstrumentId);

                Assert.Equal(18950m, buyer.CashBalance);
                Assert.Equal(18950m, buyer.AvailableCash);
                Assert.Equal(0m, buyer.ReservedCash);
                Assert.Equal(1250m, seller.CashBalance);
                Assert.Equal(1250m, seller.AvailableCash);
                Assert.Equal(10, buyerPosition.Quantity);
                Assert.Equal(105m, buyerPosition.AverageCost);
                Assert.Equal(0, buyerPosition.ReservedQuantity);
                Assert.Equal(90, sellerPosition.Quantity);
                Assert.Equal(0, sellerPosition.ReservedQuantity);

                var buy = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == buyId);
                var sellOne = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == sellOneId);
                var sellTwo = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == sellTwoId);
                Assert.Equal(10, buy.FilledQuantity);
                Assert.Equal(OrderStatus.Filled, buy.Status);
                Assert.Equal(4, sellOne.FilledQuantity);
                Assert.Equal(OrderStatus.Filled, sellOne.Status);
                Assert.Equal(6, sellTwo.FilledQuantity);
                Assert.Equal(OrderStatus.Filled, sellTwo.Status);

                var executions = await PersistedExecutionsAsync(verify, f);
                Assert.Equal(2, executions.Count);
                Assert.Equal(10, executions.Sum(e => e.ExecutedQuantity));
                Assert.All(executions, e => Assert.Equal(105m, e.ExecutedPrice));
                Assert.Contains(executions, e => e.BuyOrderId == buyId && e.SellOrderId == sellOneId && e.ExecutedQuantity == 4);
                Assert.Contains(executions, e => e.BuyOrderId == buyId && e.SellOrderId == sellTwoId && e.ExecutedQuantity == 6);
            }

            // Third process: completed orders do not re-enter the executable book.
            await using (var db = CreateDb())
            {
                var engine = new OrderMatchingEngine();
                var coordinator = CreateCoordinator(db, engine);
                await coordinator.RecoverOrderBookAsync();
                Assert.Null(engine.ProposeMatch(f.InstrumentId));
                Assert.Empty(await coordinator.MatchAndSettleAsync(f.InstrumentId));
            }
            await using (var verify = CreateDb())
                Assert.Equal(2, (await PersistedExecutionsAsync(verify, f)).Count);
        }
        finally { await CleanupAsync(f); }
    }

    // TEST 2: Non-crossing orders are persisted and reserved but must never settle.
    [Fact]
    public async Task EndToEnd_NonCrossingOrders_SurviveRestartWithoutExecution()
    {
        var f = await SeedAsync();
        try
        {
            await using (var db = CreateDb())
            {
                var coordinator = CreateCoordinator(db, new OrderMatchingEngine());
                await coordinator.PlaceBuyOrderAsync(OrderFor(f, OrderSide.Buy, 5, 90m));
                await coordinator.PlaceSellOrderAsync(OrderFor(f, OrderSide.Sell, 5, 100m));
                Assert.Empty(await coordinator.MatchAndSettleAsync(f.InstrumentId));
            }
            await using (var db = CreateDb())
            {
                var engine = new OrderMatchingEngine();
                var coordinator = CreateCoordinator(db, engine);
                await coordinator.RecoverOrderBookAsync();
                Assert.Null(engine.ProposeMatch(f.InstrumentId));
                Assert.Empty(await coordinator.MatchAndSettleAsync(f.InstrumentId));
            }
            await using var verify = CreateDb();
            Assert.Empty(await PersistedExecutionsAsync(verify, f));
            Assert.Equal(2, await verify.Orders.CountAsync(o => o.InstrumentId == f.InstrumentId && o.Status == OrderStatus.Pending));
            Assert.Equal(450m, (await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.BuyerId)).ReservedCash);
            Assert.Equal(5, (await verify.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.SellerId && p.InstrumentId == f.InstrumentId)).ReservedQuantity);
        }
        finally { await CleanupAsync(f); }
    }

    // TEST 3: One settlement call can consume multiple resting orders.
    [Fact]
    public async Task EndToEnd_TwoSellsOneBuy_SettlesTwoExecutionsInSingleCall()
    {
        var f = await SeedAsync();
        try
        {
            await using var db = CreateDb();
            var engine = new OrderMatchingEngine();
            var coordinator = CreateCoordinator(db, engine);
            var buy = await coordinator.PlaceBuyOrderAsync(OrderFor(f, OrderSide.Buy, 10, 105m));
            await coordinator.PlaceSellOrderAsync(OrderFor(f, OrderSide.Sell, 4, 100m));
            await coordinator.PlaceSellOrderAsync(OrderFor(f, OrderSide.Sell, 6, 101m));
            var result = await coordinator.MatchAndSettleAsync(f.InstrumentId);
            Assert.Equal(2, result.Count);
            Assert.Equal(10, result.Sum(e => e.ExecutedQuantity));
            await using var verify = CreateDb();
            Assert.Equal(2, (await PersistedExecutionsAsync(verify, f)).Count);
            Assert.Equal(OrderStatus.Filled, (await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == buy.OrderId)).Status);
            Assert.Equal(0m, (await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.BuyerId)).ReservedCash);
        }
        finally { await CleanupAsync(f); }
    }
}
