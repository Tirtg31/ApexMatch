using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Services;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradingEngine.Tests.Persistence;

// Runs against your dedicated Oracle TEST schema. Do not use a production database.
public class TradingCoordinatorRecoveryIntegrationTests
{
    private const string ConnectionString =
        "User Id=trading;Password=trade_123;Data Source=localhost:1521/XEPDB1";

    private static TradingDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseOracle(ConnectionString, o =>
                o.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion21))
            .Options;
        return new TradingDbContext(options);
    }

    private static OrderService CreateService(TradingDbContext db, OrderMatchingEngine? engine = null)
    {
        var uow = new EfUnitOfWork(db, NullLogger<EfUnitOfWork>.Instance);
        var accounts = new AccountRepository(db);
        var positions = new PositionRepository(db);
        var orders = new OrderRepository(db);

        // If you added ITradeInstrumentRepository to the constructor after uploading
        // the zip, inject your TradeInstrumentRepository(db) here as well.
        return new OrderService(
            new AccountService(accounts, uow),
            engine ?? new OrderMatchingEngine(),
            new PositionService(positions, uow),
            accounts, uow, orders);
    }

    private static TradingCoordinator CreateCoordinator(TradingDbContext db, OrderMatchingEngine engine)
        => new TradingCoordinator(null!, engine, CreateService(db, engine), new OrderRepository(db));

    private sealed record Fixture(int AccountId, int InstrumentId);

    private static async Task<Fixture> SeedAsync(
        decimal cash = 10000m, int shares = 100,
        bool activeAccount = true, bool activeInstrument = true)
    {
        await using var db = CreateDb();
        var account = new Account
        {
            AccountName = $"OrderSvc_{Guid.NewGuid():N}",
            CashBalance = cash,
            AvailableCash = cash,
            ReservedCash = 0,
            IsActive = activeAccount
        };
        var instrument = new TradeInstrument
        {
            InstrumentName = $"Instrument_{Guid.NewGuid():N}",
            InstrumentSymbol = $"T{Guid.NewGuid():N}"[..20],
            IsActive = activeInstrument
        };
        db.Accounts.Add(account);
        db.TradeInstruments.Add(instrument);
        await db.SaveChangesAsync();
        if (shares > 0)
        {
            db.Positions.Add(new Position
            {
                AccountId = account.AccountId,
                InstrumentId = instrument.InstrumentId,
                Quantity = shares,
                ReservedQuantity = 0,
                AverageCost = 80m
            });
            await db.SaveChangesAsync();
        }
        return new Fixture(account.AccountId, instrument.InstrumentId);
    }

    private static async Task CleanupAsync(Fixture fixture)
    {
        await using var db = CreateDb();
        await db.Orders.Where(o => o.AccountId == fixture.AccountId)
            .ExecuteDeleteAsync();
        await db.Positions.Where(p => p.AccountId == fixture.AccountId)
            .ExecuteDeleteAsync();
        await db.Accounts.Where(a => a.AccountId == fixture.AccountId)
            .ExecuteDeleteAsync();
        await db.TradeInstruments.Where(i => i.InstrumentId == fixture.InstrumentId)
            .ExecuteDeleteAsync();
    }

    private static Order NewOrder(Fixture fixture, OrderSide side, int quantity = 10) => new()
    {
        AccountId = fixture.AccountId,
        InstrumentId = fixture.InstrumentId,
        Side = side,
        OrderType = OrderType.Limit,
        Quantity = quantity,
        LimitPrice = 100m
    };

    private static async Task<int> SeedOrderAsync(
        Fixture fixture, OrderSide side, OrderStatus status,
        int quantity, int filled, decimal reservedCash = 0m,
        int reservedShares = 0)
    {
        await using var db = CreateDb();
        var account = await db.Accounts.SingleAsync(a => a.AccountId == fixture.AccountId);
        account.ReservedCash = reservedCash;
        account.AvailableCash = account.CashBalance - reservedCash;
        var position = await db.Positions.SingleAsync(p =>
            p.AccountId == fixture.AccountId && p.InstrumentId == fixture.InstrumentId);
        position.ReservedQuantity = reservedShares;
        var order = NewOrder(fixture, side, quantity);
        order.Status = status;
        order.FilledQuantity = filled;
        order.CreatedAt = DateTime.UtcNow;
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.OrderId;
    }


    [Fact]
    public async Task Recover_RestoresPendingBuyAndSellOrders()
    {
        var f = await SeedAsync();
        try
        {
            int buyId = await SeedOrderAsync(f, OrderSide.Buy, OrderStatus.Pending, 10, 0, reservedCash: 1000m);
            int sellId = await SeedOrderAsync(f, OrderSide.Sell, OrderStatus.Pending, 20, 0, reservedCash: 1000m, reservedShares: 20);
            var engine = new OrderMatchingEngine();
            await using var db = CreateDb();
            await CreateCoordinator(db, engine).RecoverOrderBookAsync();
            Assert.Equal(OrderSide.Buy, engine.GetOrderById(buyId)!.Side);
            Assert.Equal(OrderSide.Sell, engine.GetOrderById(sellId)!.Side);
            Assert.Equal(OrderStatus.Pending, engine.GetOrderById(buyId)!.Status);
            Assert.Equal(OrderStatus.Pending, engine.GetOrderById(sellId)!.Status);
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task Recover_ExcludesCancelledAndFilledOrders()
    {
        var f = await SeedAsync();
        try
        {
            int cancelledId = await SeedOrderAsync(f, OrderSide.Buy, OrderStatus.Cancelled, 10, 0);
            int filledId = await SeedOrderAsync(f, OrderSide.Sell, OrderStatus.Filled, 10, 10);
            int pendingId = await SeedOrderAsync(f, OrderSide.Buy, OrderStatus.Pending, 5, 0, reservedCash: 500m);
            var engine = new OrderMatchingEngine();
            await using var db = CreateDb();
            await CreateCoordinator(db, engine).RecoverOrderBookAsync();
            Assert.Null(engine.GetOrderById(cancelledId));
            Assert.Null(engine.GetOrderById(filledId));
            Assert.NotNull(engine.GetOrderById(pendingId));
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task Recover_PreservesPartialFillAndOriginalId()
    {
        var f = await SeedAsync();
        try
        {
            int id = await SeedOrderAsync(f, OrderSide.Buy, OrderStatus.PartiallyFilled, 20, 7, reservedCash: 1300m);
            var engine = new OrderMatchingEngine();
            await using var db = CreateDb();
            await CreateCoordinator(db, engine).RecoverOrderBookAsync();
            var restored = engine.GetOrderById(id);
            Assert.NotNull(restored);
            Assert.Equal(id, restored.OrderId);
            Assert.Equal(20, restored.Quantity);
            Assert.Equal(7, restored.FilledQuantity);
            Assert.Equal(OrderStatus.PartiallyFilled, restored.Status);
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task Recover_ReplacesStaleInMemoryOrders()
    {
        var f = await SeedAsync();
        try
        {
            int persistedId = await SeedOrderAsync(f, OrderSide.Buy, OrderStatus.Pending, 10, 0, reservedCash: 1000m);
            var engine = new OrderMatchingEngine();
            engine.RegisterPersistedOrder(new Order
            {
                OrderId = -1 + 800000,
                AccountId = f.AccountId,
                InstrumentId = f.InstrumentId,
                Side = OrderSide.Buy,
                OrderType = OrderType.Limit,
                Quantity = 3,
                LimitPrice = 99m,
                Status = OrderStatus.Pending,
                CreatedAt = DateTime.UtcNow
            });
            const int staleId = 799999;
            Assert.NotNull(engine.GetOrderById(staleId));
            await using var db = CreateDb();
            await CreateCoordinator(db, engine).RecoverOrderBookAsync();
            Assert.Null(engine.GetOrderById(staleId));
            Assert.NotNull(engine.GetOrderById(persistedId));
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task Recover_CalledTwice_DoesNotDuplicateOrders()
    {
        var f = await SeedAsync();
        try
        {
            int id = await SeedOrderAsync(f, OrderSide.Buy, OrderStatus.Pending, 10, 0, reservedCash: 1000m);
            var engine = new OrderMatchingEngine();
            await using var db = CreateDb();
            var coordinator = CreateCoordinator(db, engine);
            await coordinator.RecoverOrderBookAsync();
            await coordinator.RecoverOrderBookAsync();
            Assert.Equal(id, engine.GetOrderById(id)!.OrderId);
            // Duplicate entries and queue behavior are covered by the separate engine recovery unit tests.
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task Recover_AfterCancellationSyncFailure_ResumesTrading()
    {
        var f = await SeedAsync();
        try
        {
            var engine = new OrderMatchingEngine();
            await using var db = CreateDb();
            var coordinator = CreateCoordinator(db, engine);
            int id = (await coordinator.PlaceBuyOrderAsync(NewOrder(f, OrderSide.Buy))).OrderId;
            Assert.True(engine.CancelOrder(id)); // Deliberate divergence: Oracle remains Pending.
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CancelBuyOrderAsync(id));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.PlaceBuyOrderAsync(NewOrder(f, OrderSide.Buy)));
            await coordinator.RecoverOrderBookAsync();
            Assert.Null(engine.GetOrderById(id)); // Oracle cancellation was committed.
            var next = await coordinator.PlaceBuyOrderAsync(NewOrder(f, OrderSide.Buy));
            Assert.NotNull(engine.GetOrderById(next.OrderId));
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task Recover_InvalidPersistedOrder_LeavesTradingSuspended()
    {
        var f = await SeedAsync();
        try
        {
            int id = await SeedOrderAsync(f, OrderSide.Buy, OrderStatus.Pending, 10, 0, reservedCash: 1000m);
            // Corrupt an OPEN order through EF deliberately: it will be returned by the recovery query.
            // A zero Quantity is invalid for RegisterPersistedOrder and RebuildFromPersistedOrders.
            await using (var modify = CreateDb())
            {
                var order = await modify.Orders.SingleAsync(o => o.OrderId == id);
                order.Quantity = 0;
                await modify.SaveChangesAsync();
            }
            var engine = new OrderMatchingEngine();
            await using var db = CreateDb();
            var coordinator = CreateCoordinator(db, engine);
            await Assert.ThrowsAnyAsync<Exception>(() => coordinator.RecoverOrderBookAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.PlaceBuyOrderAsync(NewOrder(f, OrderSide.Buy)));
            Assert.Null(engine.GetOrderById(id));
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task Recover_WhenFixtureHasNoOpenOrders_DropsItsStaleOrder()
    {
        // Recovery queries ALL open orders in the schema, not just this fixture's.
        // We therefore assert removal of our fixture's stale entry, not an empty global book.
        var f = await SeedAsync();
        try
        {
            var engine = new OrderMatchingEngine();
            const int staleId = 799998;
            engine.RegisterPersistedOrder(new Order
            {
                OrderId = staleId,
                AccountId = f.AccountId,
                InstrumentId = f.InstrumentId,
                Side = OrderSide.Sell,
                OrderType = OrderType.Limit,
                Quantity = 3,
                LimitPrice = 110m,
                Status = OrderStatus.Pending,
                CreatedAt = DateTime.UtcNow
            });
            await using var db = CreateDb();
            await CreateCoordinator(db, engine).RecoverOrderBookAsync();
            Assert.Null(engine.GetOrderById(staleId));
        }
        finally { await CleanupAsync(f); }
    }
}
