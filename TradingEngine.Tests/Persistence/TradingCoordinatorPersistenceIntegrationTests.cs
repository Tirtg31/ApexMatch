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
public class TradingCoordinatorPersistenceIntegrationTests
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
        => new TradingCoordinator(null!, engine, CreateService(db, engine));

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
    public async Task PlaceBuyOrderAsync_ValidOrder_PersistsAndRegisters()
    {
        var f = await SeedAsync();
        try
        {
            var engine = new OrderMatchingEngine();
            int id;
            await using (var db = CreateDb())
            {
                var order = await CreateCoordinator(db, engine).PlaceBuyOrderAsync(NewOrder(f, OrderSide.Buy));
                id = order.OrderId;
            }
            var inBook = engine.GetOrderById(id);
            Assert.NotNull(inBook);
            Assert.Equal(id, inBook.OrderId);
            Assert.Equal(OrderStatus.Pending, inBook.Status);
            Assert.Equal(10, inBook.Quantity);
            await using var check = CreateDb();
            var saved = await check.Orders.AsNoTracking().SingleAsync(o => o.OrderId == id);
            var account = await check.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.AccountId);
            Assert.Equal(OrderStatus.Pending, saved.Status);
            Assert.Equal(saved.CreatedAt, inBook.CreatedAt);
            Assert.Equal(1000m, account.ReservedCash);
            Assert.Equal(9000m, account.AvailableCash);
            Assert.False(await check.Executions.AnyAsync(e => e.BuyOrderId == id));
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task PlaceSellOrderAsync_ValidOrder_PersistsAndRegisters()
    {
        var f = await SeedAsync();
        try
        {
            var engine = new OrderMatchingEngine();
            int id;
            await using (var db = CreateDb())
                id = (await CreateCoordinator(db, engine).PlaceSellOrderAsync(NewOrder(f, OrderSide.Sell, 20))).OrderId;
            var inBook = engine.GetOrderById(id);
            Assert.NotNull(inBook);
            Assert.Equal(OrderSide.Sell, inBook.Side);
            Assert.Equal(20, inBook.Quantity);
            Assert.Equal(OrderStatus.Pending, inBook.Status);
            await using var check = CreateDb();
            var saved = await check.Orders.AsNoTracking().SingleAsync(o => o.OrderId == id);
            var pos = await check.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.AccountId);
            Assert.Equal(id, saved.OrderId);
            Assert.Equal(20, pos.ReservedQuantity);
            Assert.Equal(80, pos.AvailableQuantity);
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task CancelBuyOrderAsync_ValidOrder_CancelsInDbAndEngine()
    {
        var f = await SeedAsync();
        try
        {
            var engine = new OrderMatchingEngine();
            int id;
            await using (var db = CreateDb())
            {
                var sut = CreateCoordinator(db, engine);
                id = (await sut.PlaceBuyOrderAsync(NewOrder(f, OrderSide.Buy))).OrderId;
                await sut.CancelBuyOrderAsync(id);
            }
            Assert.Equal(OrderStatus.Cancelled, engine.GetOrderById(id)!.Status);
            await using var check = CreateDb();
            Assert.Equal(OrderStatus.Cancelled, (await check.Orders.AsNoTracking().SingleAsync(o => o.OrderId == id)).Status);
            var account = await check.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.AccountId);
            Assert.Equal(0m, account.ReservedCash);
            Assert.Equal(10000m, account.AvailableCash);
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task CancelSellOrderAsync_ValidOrder_CancelsInDbAndEngine()
    {
        var f = await SeedAsync();
        try
        {
            var engine = new OrderMatchingEngine();
            int id;
            await using (var db = CreateDb())
            {
                var sut = CreateCoordinator(db, engine);
                id = (await sut.PlaceSellOrderAsync(NewOrder(f, OrderSide.Sell, 20))).OrderId;
                await sut.CancelSellOrderAsync(id);
            }
            Assert.Equal(OrderStatus.Cancelled, engine.GetOrderById(id)!.Status);
            await using var check = CreateDb();
            Assert.Equal(OrderStatus.Cancelled, (await check.Orders.AsNoTracking().SingleAsync(o => o.OrderId == id)).Status);
            var pos = await check.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.AccountId);
            Assert.Equal(0, pos.ReservedQuantity);
            Assert.Equal(100, pos.AvailableQuantity);
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task PlaceBuyOrderAsync_InsufficientFunds_DoesNotRegister()
    {
        var f = await SeedAsync(cash: 500m);
        try
        {
            var engine = new OrderMatchingEngine();
            await using (var db = CreateDb())
            {
                var sut = CreateCoordinator(db, engine);
                await Assert.ThrowsAnyAsync<Exception>(() => sut.PlaceBuyOrderAsync(NewOrder(f, OrderSide.Buy)));
            }
            await using var check = CreateDb();
            Assert.False(await check.Orders.AnyAsync(o => o.AccountId == f.AccountId));
            var account = await check.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.AccountId);
            Assert.Equal(500m, account.AvailableCash);
            Assert.Equal(0m, account.ReservedCash);
            // A subsequent valid order proves the coordinator gate was released
            // and the normal rejection did not put the engine in recovery mode.
            await using var db2 = CreateDb();
            var sut2 = CreateCoordinator(db2, engine);
            var small = NewOrder(f, OrderSide.Buy);
            small.Quantity = 1;
            var placed = await sut2.PlaceBuyOrderAsync(small);
            Assert.NotNull(engine.GetOrderById(placed.OrderId));
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task CancelBuyOrderAsync_AlreadyCancelled_RejectsWithoutChanges()
    {
        var f = await SeedAsync();
        try
        {
            var engine = new OrderMatchingEngine();
            await using var db = CreateDb();
            var sut = CreateCoordinator(db, engine);
            int id = (await sut.PlaceBuyOrderAsync(NewOrder(f, OrderSide.Buy))).OrderId;
            await sut.CancelBuyOrderAsync(id);
            await Assert.ThrowsAnyAsync<Exception>(() => sut.CancelBuyOrderAsync(id));
            Assert.Equal(OrderStatus.Cancelled, engine.GetOrderById(id)!.Status);
            await using var check = CreateDb();
            var account = await check.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.AccountId);
            Assert.Equal(0m, account.ReservedCash);
            Assert.Equal(10000m, account.AvailableCash);
            Assert.Equal(OrderStatus.Cancelled, (await check.Orders.AsNoTracking().SingleAsync(o => o.OrderId == id)).Status);
            // If the coordinator incorrectly required recovery, this throws.
            var subsequent = NewOrder(f, OrderSide.Buy);
            var next = await sut.PlaceBuyOrderAsync(subsequent);
            Assert.NotNull(engine.GetOrderById(next.OrderId));
        }
        finally { await CleanupAsync(f); }
    }

    // Current OrderMatchingEngine methods are non-virtual and coordinator
    // takes a concrete engine. A deterministic registration-failure test needs
    // an injectable book interface; do not guess Oracle's next generated ID.
    [Fact(Skip = "Requires injectable IOrderBookGateway/engine failure seam for deterministic registration failure")]
    public Task PlaceBuyOrderAsync_BookRegistrationFails_RequiresRecovery()
        => Task.CompletedTask;

    [Fact]
    public async Task CancelBuyOrderAsync_BookCancellationFails_RequiresRecovery()
    {
        var f = await SeedAsync();
        try
        {
            var engine = new OrderMatchingEngine();
            await using var db = CreateDb();
            var sut = CreateCoordinator(db, engine);
            int id = (await sut.PlaceBuyOrderAsync(NewOrder(f, OrderSide.Buy))).OrderId;
            // Controlled mismatch: the in-memory order is already cancelled,
            // while Oracle still considers it Pending. Thus CancelOrder returns false.
            Assert.True(engine.CancelOrder(id));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.CancelBuyOrderAsync(id));
            Assert.Contains("suspended", ex.Message, StringComparison.OrdinalIgnoreCase);
            await using (var check = CreateDb())
            {
                Assert.Equal(OrderStatus.Cancelled,
                    (await check.Orders.AsNoTracking().SingleAsync(o => o.OrderId == id)).Status);
                var account = await check.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.AccountId);
                Assert.Equal(0m, account.ReservedCash);
                Assert.Equal(10000m, account.AvailableCash);
            }
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => sut.PlaceBuyOrderAsync(NewOrder(f, OrderSide.Buy)));
            await using var verify = CreateDb();
            Assert.Equal(1, await verify.Orders.CountAsync(o => o.AccountId == f.AccountId));
        }
        finally { await CleanupAsync(f); }
    }
}
