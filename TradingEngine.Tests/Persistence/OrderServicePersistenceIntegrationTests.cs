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
public class OrderServicePersistenceIntegrationTests
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

    private static OrderService CreateService(TradingDbContext db)
    {
        var uow = new EfUnitOfWork(db, NullLogger<EfUnitOfWork>.Instance);
        var accounts = new AccountRepository(db);
        var positions = new PositionRepository(db);
        var orders = new OrderRepository(db);

        // If you added ITradeInstrumentRepository to the constructor after uploading
        // the zip, inject your TradeInstrumentRepository(db) here as well.
        return new OrderService(
            new AccountService(accounts, uow),
            new OrderMatchingEngine(),
            new PositionService(positions, uow),
            accounts, uow, orders);
    }

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

    // 1: Placing a BUY persists the order and reserves exactly qty * limit price.
    [Fact]
    public async Task PlaceBuyOrder_ReservesCash_AndPersistsOrder()
    {
        var f = await SeedAsync();
        try
        {
            int id;
            await using (var db = CreateDb())
            {
                var order = await CreateService(db).PlaceBuyOrderAsync(
                    NewOrder(f, OrderSide.Buy));
                id = order.OrderId;
            }
            await using var verify = CreateDb();
            var saved = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == id);
            var account = await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.AccountId);
            Assert.True(id > 0);
            Assert.Equal(OrderStatus.Pending, saved.Status);
            Assert.Equal(0, saved.FilledQuantity);
            Assert.Equal(1000m, account.ReservedCash);
            Assert.Equal(9000m, account.AvailableCash);
            Assert.Equal(10000m, account.CashBalance);
        }
        finally { await CleanupAsync(f); }
    }

    // 2: Placing a SELL reserves shares, not cash.
    [Fact]
    public async Task PlaceSellOrder_ReservesShares_AndPersistsOrder()
    {
        var f = await SeedAsync();
        try
        {
            int id;
            await using (var db = CreateDb())
                id = (await CreateService(db).PlaceSellOrderAsync(NewOrder(f, OrderSide.Sell))).OrderId;
            await using var verify = CreateDb();
            var saved = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == id);
            var pos = await verify.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.AccountId);
            Assert.Equal(OrderStatus.Pending, saved.Status);
            Assert.Equal(10, pos.ReservedQuantity);
            Assert.Equal(100, pos.Quantity);
            Assert.Equal(90, pos.AvailableQuantity);
        }
        finally { await CleanupAsync(f); }
    }

    // 3: Cancel pending BUY: release all reserved cash.
    [Fact]
    public async Task CancelBuy_Pending_ReleasesAllReservedCash()
    {
        var f = await SeedAsync();
        try
        {
            int id = await SeedOrderAsync(f, OrderSide.Buy, OrderStatus.Pending, 10, 0, reservedCash: 1000m);
            await using (var db = CreateDb())
                await CreateService(db).CancelBuyOrderAsync(id);
            await using var verify = CreateDb();
            var order = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == id);
            var account = await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.AccountId);
            Assert.Equal(OrderStatus.Cancelled, order.Status);
            Assert.Equal(0m, account.ReservedCash);
            Assert.Equal(10000m, account.AvailableCash);
        }
        finally { await CleanupAsync(f); }
    }

    // 4: Cancel partially filled BUY: release only (qty - filled) * limit price.
    [Fact]
    public async Task CancelBuy_PartiallyFilled_ReleasesOnlyRemainingCash()
    {
        var f = await SeedAsync();
        try
        {
            int id = await SeedOrderAsync(f, OrderSide.Buy, OrderStatus.PartiallyFilled,
                10, 4, reservedCash: 600m);
            await using (var db = CreateDb())
                await CreateService(db).CancelBuyOrderAsync(id);
            await using var verify = CreateDb();
            var order = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == id);
            var account = await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.AccountId);
            Assert.Equal(OrderStatus.Cancelled, order.Status);
            Assert.Equal(4, order.FilledQuantity);
            Assert.Equal(0m, account.ReservedCash);
            Assert.Equal(10000m, account.AvailableCash);
        }
        finally { await CleanupAsync(f); }
    }

    // 5: Cancel pending SELL: release all remaining reserved shares.
    [Fact]
    public async Task CancelSell_Pending_ReleasesReservedShares()
    {
        var f = await SeedAsync();
        try
        {
            int id = await SeedOrderAsync(f, OrderSide.Sell, OrderStatus.Pending,
                10, 0, reservedShares: 10);
            await using (var db = CreateDb())
                await CreateService(db).CancelSellOrderAsync(id);
            await using var verify = CreateDb();
            var order = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == id);
            var pos = await verify.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.AccountId);
            Assert.Equal(OrderStatus.Cancelled, order.Status);
            Assert.Equal(0, pos.ReservedQuantity);
            Assert.Equal(100, pos.AvailableQuantity);
        }
        finally { await CleanupAsync(f); }
    }

    // 6: Cancel partially filled SELL: release only unfilled shares.
    [Fact]
    public async Task CancelSell_PartiallyFilled_ReleasesOnlyRemainingShares()
    {
        var f = await SeedAsync();
        try
        {
            int id = await SeedOrderAsync(f, OrderSide.Sell, OrderStatus.PartiallyFilled,
                10, 4, reservedShares: 6);
            await using (var db = CreateDb())
                await CreateService(db).CancelSellOrderAsync(id);
            await using var verify = CreateDb();
            var order = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == id);
            var pos = await verify.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.AccountId);
            Assert.Equal(OrderStatus.Cancelled, order.Status);
            Assert.Equal(4, order.FilledQuantity);
            Assert.Equal(0, pos.ReservedQuantity);
        }
        finally { await CleanupAsync(f); }
    }

    // 7: Repeated BUY cancellation must not release money twice.
    [Fact]
    public async Task CancelBuy_AlreadyCancelled_RejectsAndDoesNotChangeBalances()
    {
        var f = await SeedAsync();
        try
        {
            int id = await SeedOrderAsync(f, OrderSide.Buy, OrderStatus.Pending,
                10, 0, reservedCash: 1000m);
            await using (var db = CreateDb())
                await CreateService(db).CancelBuyOrderAsync(id);
            await using (var db = CreateDb())
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    CreateService(db).CancelBuyOrderAsync(id));
            await using var verify = CreateDb();
            var account = await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.AccountId);
            Assert.Equal(0m, account.ReservedCash);
            Assert.Equal(10000m, account.AvailableCash);
        }
        finally { await CleanupAsync(f); }
    }

    // 8: Fully filled order cannot be cancelled.
    [Fact]
    public async Task CancelSell_Filled_RejectsAndDoesNotChangeShares()
    {
        var f = await SeedAsync();
        try
        {
            int id = await SeedOrderAsync(f, OrderSide.Sell, OrderStatus.Filled, 10, 10);
            await using (var db = CreateDb())
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    CreateService(db).CancelSellOrderAsync(id));
            await using var verify = CreateDb();
            var order = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == id);
            var pos = await verify.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.AccountId);
            Assert.Equal(OrderStatus.Filled, order.Status);
            Assert.Equal(0, pos.ReservedQuantity);
        }
        finally { await CleanupAsync(f); }
    }
    // 10: Inactive account cannot place BUY or SELL orders.
    [Theory]
    [InlineData(OrderSide.Buy)]
    [InlineData(OrderSide.Sell)]
    public async Task PlaceOrder_InactiveAccount_RejectsWithoutChanges(OrderSide side)
    {
        var f = await SeedAsync(activeAccount: false);
        try
        {
            await using (var db = CreateDb())
            {
                var service = CreateService(db);
                if (side == OrderSide.Buy)
                    await Assert.ThrowsAnyAsync<Exception>(() => service.PlaceBuyOrderAsync(NewOrder(f, side)));
                else
                    await Assert.ThrowsAnyAsync<Exception>(() => service.PlaceSellOrderAsync(NewOrder(f, side)));
            }
            await using var verify = CreateDb();
            Assert.False(await verify.Orders.AnyAsync(o => o.AccountId == f.AccountId));
            var account = await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.AccountId);
            var pos = await verify.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.AccountId);
            Assert.Equal(0m, account.ReservedCash);
            Assert.Equal(0, pos.ReservedQuantity);
        }
        finally { await CleanupAsync(f); }
    }

    // 11: If release validation fails, cancellation transaction rolls back.
    [Fact]
    public async Task CancelBuy_InsufficientReservedCash_RollsBackStatus()
    {
        var f = await SeedAsync();
        try
        {
            int id = await SeedOrderAsync(f, OrderSide.Buy, OrderStatus.Pending,
                10, 0, reservedCash: 500m); // needs 1000, only 500 reserved
            await using (var db = CreateDb())
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    CreateService(db).CancelBuyOrderAsync(id));
            await using var verify = CreateDb();
            var order = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == id);
            var account = await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.AccountId);
            Assert.Equal(OrderStatus.Pending, order.Status);
            Assert.Equal(500m, account.ReservedCash);
            Assert.Equal(9500m, account.AvailableCash);
        }
        finally { await CleanupAsync(f); }
    }

    // 12: Insufficient shares on placement must not create an order.
    [Fact]
    public async Task PlaceSell_InsufficientShares_DoesNotPersistOrder()
    {
        var f = await SeedAsync(shares: 5);
        try
        {
            await using (var db = CreateDb())
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    CreateService(db).PlaceSellOrderAsync(NewOrder(f, OrderSide.Sell, quantity: 10)));
            await using var verify = CreateDb();
            Assert.False(await verify.Orders.AnyAsync(o => o.AccountId == f.AccountId));
            var pos = await verify.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.AccountId);
            Assert.Equal(0, pos.ReservedQuantity);
        }
        finally { await CleanupAsync(f); }
    }
}
