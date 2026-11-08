using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradingEngine.Tests.Persistence;

// Oracle integration tests. Use ONLY a disposable test schema.
// Replace the connection string below with the same test credentials you used previously.
public class ExecutionRepositoryIntegrationTests
{
    private const string ConnectionString =
        "User Id=trading;Password=trade_123;Data Source=localhost:1521/XEPDB1";

    private TradingDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseOracle(ConnectionString, oracle => oracle.UseOracleSQLCompatibility(
                OracleSQLCompatibility.DatabaseVersion21))
            .Options;
        return new TradingDbContext(options);
    }

    private static ExecutionRepository Repo(TradingDbContext db) => new(db);
    private static EfUnitOfWork Uow(TradingDbContext db) =>
        new(db, NullLogger<EfUnitOfWork>.Instance);

    private sealed record Seed(int BuyerId, int SellerId, int InstrumentId,
        int BuyOrderId, int SellOrderId);

    // Inserts dedicated parents so Execution foreign keys are valid.
    private async Task<Seed> SeedOrdersAsync()
    {
        await using var db = CreateDbContext();
        var token = Guid.NewGuid().ToString("N")[..12];
        var buyer = new Account
        {
            AccountName = $"ExBuyer_{token}",
            CashBalance = 10000m,
            AvailableCash = 10000m,
            ReservedCash = 0m,
            IsActive = true
        };
        var seller = new Account
        {
            AccountName = $"ExSeller_{token}",
            CashBalance = 10000m,
            AvailableCash = 10000m,
            ReservedCash = 0m,
            IsActive = true
        };
        var instrument = new TradeInstrument
        {
            InstrumentName = $"Execution Test {token}",
            InstrumentSymbol = $"E{token}",
            IsActive = true
        };
        db.Accounts.AddRange(buyer, seller);
        db.TradeInstruments.Add(instrument);
        await db.SaveChangesAsync();

        var buyOrder = new Order
        {
            AccountId = buyer.AccountId,
            InstrumentId = instrument.InstrumentId,
            Side = OrderSide.Buy,
            OrderType = OrderType.Limit,
            Quantity = 100,
            LimitPrice = 100m,
            FilledQuantity = 0,
            Status = OrderStatus.Pending,
            CreatedAt = DateTime.UtcNow.AddMinutes(-2)
        };
        var sellOrder = new Order
        {
            AccountId = seller.AccountId,
            InstrumentId = instrument.InstrumentId,
            Side = OrderSide.Sell,
            OrderType = OrderType.Limit,
            Quantity = 100,
            LimitPrice = 100m,
            FilledQuantity = 0,
            Status = OrderStatus.Pending,
            CreatedAt = DateTime.UtcNow.AddMinutes(-1)
        };
        db.Orders.AddRange(buyOrder, sellOrder);
        await db.SaveChangesAsync();
        return new Seed(buyer.AccountId, seller.AccountId, instrument.InstrumentId,
            buyOrder.OrderId, sellOrder.OrderId);
    }

    private static Execution MakeExecution(Seed s, int quantity = 10, decimal price = 100m) =>
        new()
        {
            BuyOrderId = s.BuyOrderId,
            SellOrderId = s.SellOrderId,
            ExecutedQuantity = quantity,
            ExecutedPrice = price,
            ExecutedAt = DateTime.UtcNow
        };

    private async Task CleanupAsync(Seed s)
    {
        await using var db = CreateDbContext();
        await db.Executions.Where(e => e.BuyOrderId == s.BuyOrderId ||
                                        e.SellOrderId == s.SellOrderId).ExecuteDeleteAsync();
        await db.Orders.Where(o => o.OrderId == s.BuyOrderId ||
                                    o.OrderId == s.SellOrderId).ExecuteDeleteAsync();
        await db.Accounts.Where(a => a.AccountId == s.BuyerId ||
                                      a.AccountId == s.SellerId).ExecuteDeleteAsync();
        await db.TradeInstruments.Where(t => t.InstrumentId == s.InstrumentId)
            .ExecuteDeleteAsync();
    }

    // TEST 1: AddAsync plus SaveChangesAsync persists an execution to Oracle.
    [Fact]
    public async Task AddExecution_Save_ShouldPersist()
    {
        var s = await SeedOrdersAsync();
        try
        {
            int id;
            await using (var db = CreateDbContext())
            {
                var execution = MakeExecution(s, 25, 99m);
                await Repo(db).AddAsync(execution);
                await Uow(db).SaveChangesAsync();
                id = execution.ExecutionId;
                Assert.True(id > 0);
            }
            await using var check = CreateDbContext();
            var saved = await check.Executions.AsNoTracking()
                .SingleOrDefaultAsync(e => e.ExecutionId == id);
            Assert.NotNull(saved);
            Assert.Equal(s.BuyOrderId, saved.BuyOrderId);
            Assert.Equal(s.SellOrderId, saved.SellOrderId);
            Assert.Equal(25, saved.ExecutedQuantity);
            Assert.Equal(99m, saved.ExecutedPrice);
        }
        finally { await CleanupAsync(s); }
    }

    // TEST 2: GetByIdAsync retrieves the correct persisted execution.
    [Fact]
    public async Task GetById_ExistingExecution_ReturnsExecution()
    {
        var s = await SeedOrdersAsync();
        try
        {
            int id;
            await using (var db = CreateDbContext())
            {
                var execution = MakeExecution(s);
                await Repo(db).AddAsync(execution);
                await Uow(db).SaveChangesAsync();
                id = execution.ExecutionId;
            }
            await using var check = CreateDbContext();
            var result = await Repo(check).GetByIdAsync(id);
            Assert.NotNull(result);
            Assert.Equal(id, result.ExecutionId);
        }
        finally { await CleanupAsync(s); }
    }

    // TEST 3: GetByIdAsync returns null if the execution does not exist.
    [Fact]
    public async Task GetById_MissingExecution_ReturnsNull()
    {
        await using var db = CreateDbContext();
        Assert.Null(await Repo(db).GetByIdAsync(-1));
    }

    // TEST 4: A BUY order can appear in multiple executions.
    [Fact]
    public async Task GetByOrderId_BuyOrder_ReturnsAllMatches()
    {
        var s = await SeedOrdersAsync();
        try
        {
            await using (var db = CreateDbContext())
            {
                await Repo(db).AddAsync(MakeExecution(s, 10));
                await Repo(db).AddAsync(MakeExecution(s, 20));
                await Uow(db).SaveChangesAsync();
            }
            await using var check = CreateDbContext();
            var results = await Repo(check).GetByOrderIdAsync(s.BuyOrderId);
            Assert.Equal(2, results.Count);
            Assert.All(results, e => Assert.Equal(s.BuyOrderId, e.BuyOrderId));
        }
        finally { await CleanupAsync(s); }
    }

    // TEST 5: Executions can also be retrieved using the SELL order ID.
    [Fact]
    public async Task GetByOrderId_SellOrder_ReturnsMatches()
    {
        var s = await SeedOrdersAsync();
        try
        {
            await using (var db = CreateDbContext())
            {
                await Repo(db).AddAsync(MakeExecution(s, 35));
                await Uow(db).SaveChangesAsync();
            }
            await using var check = CreateDbContext();
            var results = await Repo(check).GetByOrderIdAsync(s.SellOrderId);
            Assert.Single(results);
            Assert.Equal(s.SellOrderId, results[0].SellOrderId);
        }
        finally { await CleanupAsync(s); }
    }

    // TEST 6: An unrelated order returns no executions.
    [Fact]
    public async Task GetByOrderId_UnrelatedOrder_ReturnsEmpty()
    {
        var s = await SeedOrdersAsync();
        try
        {
            await using (var db = CreateDbContext())
            {
                await Repo(db).AddAsync(MakeExecution(s));
                await Uow(db).SaveChangesAsync();
            }
            await using var check = CreateDbContext();
            Assert.Empty(await Repo(check).GetByOrderIdAsync(-1));
        }
        finally { await CleanupAsync(s); }
    }

    // TEST 7: AddAsync alone tracks an entity but does not insert it.
    [Fact]
    public async Task AddExecution_WithoutSave_DoesNotPersist()
    {
        var s = await SeedOrdersAsync();
        try
        {
            await using (var db = CreateDbContext())
                await Repo(db).AddAsync(MakeExecution(s));
            await using var check = CreateDbContext();
            Assert.Empty(await Repo(check).GetByOrderIdAsync(s.BuyOrderId));
        }
        finally { await CleanupAsync(s); }
    }

    // TEST 8: Oracle rejects an execution referencing a nonexistent order.
    [Fact]
    public async Task AddExecution_InvalidForeignKey_ThrowsDbUpdateException()
    {
        var s = await SeedOrdersAsync();
        try
        {
            await using var db = CreateDbContext();
            var execution = MakeExecution(s);
            execution.BuyOrderId = -1;
            await Repo(db).AddAsync(execution);
            await Assert.ThrowsAsync<DbUpdateException>(() => Uow(db).SaveChangesAsync());
        }
        finally { await CleanupAsync(s); }
    }

    // TEST 9: Even an INSERT that reached Oracle is rolled back if not committed.
    [Fact]
    public async Task Transaction_ExceptionAfterSave_RollsBackExecution()
    {
        var s = await SeedOrdersAsync();
        try
        {
            await using (var db = CreateDbContext())
            {
                var repository = Repo(db);
                var uow = Uow(db);
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await uow.ExecuteInTransactionAsync<bool>(async ct =>
                    {
                        await repository.AddAsync(MakeExecution(s), ct);
                        await db.SaveChangesAsync(ct); // INSERT executed, not committed
                        throw new InvalidOperationException("Simulated settlement failure");
                    }));
            }
            await using var check = CreateDbContext();
            Assert.Empty(await Repo(check).GetByOrderIdAsync(s.BuyOrderId));
        }
        finally { await CleanupAsync(s); }
    }
}
