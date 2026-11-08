using Microsoft.EntityFrameworkCore;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradingEngine.Tests.Persistence;

// IMPORTANT: Use only a dedicated, disposable Oracle test schema.
// Replace the connection string with your TEST database credentials.
// These tests insert and delete records in the test schema.
public class OrderRepositoryIntegrationTests
{
    private TradingDbContext CreateDbContext()
    {
        const string connectionString =
            "User Id=trading;Password=trade_123;Data Source=localhost:1521/XEPDB1";

        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseOracle(connectionString, oracle =>
                oracle.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion21))
            .Options;

        return new TradingDbContext(options);
    }

    private static Account NewAccount() => new()
    {
        AccountName = $"OrderTest_{Guid.NewGuid():N}",
        CashBalance = 100_000m,
        AvailableCash = 100_000m,
        ReservedCash = 0m,
        IsActive = true
    };

    private static TradeInstrument NewInstrument() => new()
    {
        InstrumentName = $"Instrument_{Guid.NewGuid():N}",
        InstrumentSymbol = $"T{Guid.NewGuid():N}"[..20],
        IsActive = true
    };

    private static Order NewOrder(int accountId, int instrumentId,
        OrderStatus status = OrderStatus.Pending,
        DateTime? createdAt = null) => new()
        {
            AccountId = accountId,
            InstrumentId = instrumentId,
            Side = OrderSide.Buy,
            OrderType = OrderType.Limit,
            Quantity = 10,
            FilledQuantity = status == OrderStatus.PartiallyFilled ? 4 :
                         status == OrderStatus.Filled ? 10 : 0,
            LimitPrice = 100m,
            Status = status,
            CreatedAt = createdAt ?? DateTime.UtcNow
        };

    // Creates real parent rows because Order has AccountId and InstrumentId foreign keys.
    private async Task<(int accountId, int instrumentId)> SeedParentsAsync()
    {
        await using var db = CreateDbContext();
        var account = NewAccount();
        var instrument = NewInstrument();
        await db.Accounts.AddAsync(account);
        await db.TradeInstruments.AddAsync(instrument);
        await db.SaveChangesAsync();
        return (account.AccountId, instrument.InstrumentId);
    }

    private async Task CleanupAsync(params (int accountId, int instrumentId)[] parents)
    {
        await using var db = CreateDbContext();
        var accountIds = parents.Select(p => p.accountId).ToArray();
        var instrumentIds = parents.Select(p => p.instrumentId).ToArray();

        // Delete dependent orders first, then the parents.
        // Never use this cleanup helper against a non-test schema.
        await db.Orders.Where(o => accountIds.Contains(o.AccountId) &&
                                    instrumentIds.Contains(o.InstrumentId))
            .ExecuteDeleteAsync();
        await db.Accounts.Where(a => accountIds.Contains(a.AccountId))
            .ExecuteDeleteAsync();
        await db.TradeInstruments.Where(t => instrumentIds.Contains(t.InstrumentId))
            .ExecuteDeleteAsync();
    }

    // TEST 1: AddAsync + SaveChangesAsync should insert a new order in Oracle.
    [Fact]
    public async Task AddAsync_AfterSave_PersistsOrder()
    {
        var parents = await SeedParentsAsync();
        try
        {
            int orderId;
            await using (var db = CreateDbContext())
            {
                var repo = new OrderRepository(db);
                var order = NewOrder(parents.accountId, parents.instrumentId);
                await repo.AddAsync(order);
                await db.SaveChangesAsync();
                orderId = order.OrderId;
            }

            await using var verify = CreateDbContext();
            var stored = await verify.Orders.AsNoTracking()
                .SingleOrDefaultAsync(o => o.OrderId == orderId);
            Assert.NotNull(stored);
            Assert.Equal(parents.accountId, stored.AccountId);
            Assert.Equal(parents.instrumentId, stored.InstrumentId);
            Assert.Equal(OrderStatus.Pending, stored.Status);
            Assert.Equal(100m, stored.LimitPrice);
        }
        finally { await CleanupAsync(parents); }
    }

    // TEST 2: GetByIdAsync should return the matching order.
    [Fact]
    public async Task GetByIdAsync_ExistingId_ReturnsCorrectOrder()
    {
        var parents = await SeedParentsAsync();
        try
        {
            int orderId;
            await using (var db = CreateDbContext())
            {
                var order = NewOrder(parents.accountId, parents.instrumentId);
                await db.Orders.AddAsync(order);
                await db.SaveChangesAsync();
                orderId = order.OrderId;
            }

            await using var queryDb = CreateDbContext();
            var repo = new OrderRepository(queryDb);
            var result = await repo.GetByIdAsync(orderId);
            Assert.NotNull(result);
            Assert.Equal(orderId, result.OrderId);
            Assert.Equal(10, result.Quantity);
        }
        finally { await CleanupAsync(parents); }
    }

    // TEST 3: Querying a nonexistent order should return null.
    [Fact]
    public async Task GetByIdAsync_NonexistentId_ReturnsNull()
    {
        await using var db = CreateDbContext();
        var repo = new OrderRepository(db);
        Assert.Null(await repo.GetByIdAsync(-1));
    }

    // TEST 4: GetByAccountIdAsync must exclude orders of other accounts.
    [Fact]
    public async Task GetByAccountIdAsync_ReturnsOnlyRequestedAccountsOrders()
    {
        var first = await SeedParentsAsync();
        var second = await SeedParentsAsync();
        try
        {
            await using (var db = CreateDbContext())
            {
                await db.Orders.AddRangeAsync(
                    NewOrder(first.accountId, first.instrumentId),
                    NewOrder(first.accountId, first.instrumentId),
                    NewOrder(second.accountId, second.instrumentId));
                await db.SaveChangesAsync();
            }

            await using var queryDb = CreateDbContext();
            var result = await new OrderRepository(queryDb)
                .GetByAccountIdAsync(first.accountId);
            Assert.Equal(2, result.Count);
            Assert.All(result, o => Assert.Equal(first.accountId, o.AccountId));
        }
        finally { await CleanupAsync(first, second); }
    }

    // TEST 5: Only Pending and PartiallyFilled orders for the selected
    // instrument should be returned; Filled, Cancelled, Rejected are excluded.
    [Fact]
    public async Task GetOpenOrdersByInstrumentAsync_FiltersStatusAndInstrument()
    {
        var first = await SeedParentsAsync();
        var second = await SeedParentsAsync();
        try
        {
            await using (var db = CreateDbContext())
            {
                foreach (var status in new[] { OrderStatus.Pending,
                             OrderStatus.PartiallyFilled, OrderStatus.Filled,
                             OrderStatus.Cancelled, OrderStatus.Rejected })
                {
                    db.Orders.Add(NewOrder(first.accountId, first.instrumentId, status));
                }
                db.Orders.Add(NewOrder(second.accountId, second.instrumentId));
                await db.SaveChangesAsync();
            }

            await using var queryDb = CreateDbContext();
            var result = await new OrderRepository(queryDb)
                .GetOpenOrdersByInstrumentAsync(first.instrumentId);
            Assert.Equal(2, result.Count);
            Assert.Contains(result, o => o.Status == OrderStatus.Pending);
            Assert.Contains(result, o => o.Status == OrderStatus.PartiallyFilled);
            Assert.All(result, o => Assert.Equal(first.instrumentId, o.InstrumentId));
        }
        finally { await CleanupAsync(first, second); }
    }

    // TEST 6: Open orders should be returned by timestamp then OrderId.
    [Fact]
    public async Task GetOpenOrdersByInstrumentAsync_ReturnsOldestFirst()
    {
        var parents = await SeedParentsAsync();
        try
        {
            int olderId, newerId;
            await using (var db = CreateDbContext())
            {
                var older = NewOrder(parents.accountId, parents.instrumentId,
                    createdAt: new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc));
                var newer = NewOrder(parents.accountId, parents.instrumentId,
                    createdAt: new DateTime(2025, 1, 1, 11, 0, 0, DateTimeKind.Utc));
                // Insert reverse chronological order on purpose.
                db.Orders.Add(newer);
                db.Orders.Add(older);
                await db.SaveChangesAsync();
                olderId = older.OrderId;
                newerId = newer.OrderId;
            }

            await using var queryDb = CreateDbContext();
            var result = await new OrderRepository(queryDb)
                .GetOpenOrdersByInstrumentAsync(parents.instrumentId);
            Assert.Equal(new[] { olderId, newerId }, result.Select(o => o.OrderId));
        }
        finally { await CleanupAsync(parents); }
    }

    // TEST 7: Changes to tracked orders should persist after SaveChangesAsync.
    [Fact]
    public async Task GetByIdAsync_TrackedOrder_UpdateIsPersisted()
    {
        var parents = await SeedParentsAsync();
        try
        {
            int orderId;
            await using (var db = CreateDbContext())
            {
                var order = NewOrder(parents.accountId, parents.instrumentId);
                db.Orders.Add(order);
                await db.SaveChangesAsync();
                orderId = order.OrderId;
            }

            await using (var db = CreateDbContext())
            {
                var order = await new OrderRepository(db).GetByIdAsync(orderId);
                Assert.NotNull(order);
                order.FilledQuantity = 4;
                order.Status = OrderStatus.PartiallyFilled;
                await db.SaveChangesAsync();
            }

            await using var verify = CreateDbContext();
            var stored = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == orderId);
            Assert.Equal(4, stored.FilledQuantity);
            Assert.Equal(OrderStatus.PartiallyFilled, stored.Status);
        }
        finally { await CleanupAsync(parents); }
    }

    // TEST 8: AddAsync without SaveChangesAsync must not persist a row.
    [Fact]
    public async Task AddAsync_WithoutSave_DoesNotPersistOrder()
    {
        var parents = await SeedParentsAsync();
        try
        {
            var at = new DateTime(2025, 2, 3, 12, 0, 0, DateTimeKind.Utc);
            await using (var db = CreateDbContext())
            {
                await new OrderRepository(db).AddAsync(
                    NewOrder(parents.accountId, parents.instrumentId, createdAt: at));
                // Intentional: no SaveChangesAsync.
            }

            await using var verify = CreateDbContext();
            var count = await verify.Orders.AsNoTracking().CountAsync(o =>
                o.AccountId == parents.accountId &&
                o.InstrumentId == parents.instrumentId);
            Assert.Equal(0, count);
        }
        finally { await CleanupAsync(parents); }
    }
}
