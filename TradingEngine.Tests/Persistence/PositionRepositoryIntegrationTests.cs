using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Core.Entities;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradingEngine.Tests.Persistence;

// Run only against a dedicated, migrated Oracle test schema.
// Each xUnit test gets a new instance of this class and its own cleanup lists.
public class PositionRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly List<int> _accountIds = new();
    private readonly List<int> _instrumentIds = new();

    private TradingDbContext CreateDbContext()
    {
        // Replace with the SAME dedicated test-schema connection string
        // used in your successful AccountRepository integration tests.
        const string connectionString =
            "User Id=trading;Password=trade_123;Data Source=localhost:1521/XEPDB1";

        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseOracle(connectionString, oracle =>
                oracle.UseOracleSQLCompatibility(
                    OracleSQLCompatibility.DatabaseVersion21))
            .Options;

        return new TradingDbContext(options);
    }

    private static EfUnitOfWork Uow(TradingDbContext db) =>
        new(db, NullLogger<EfUnitOfWork>.Instance);

    private static Account NewAccount() => new()
    {
        AccountName = $"Acc_{Guid.NewGuid():N}",
        CashBalance = 10000m,
        AvailableCash = 10000m,
        ReservedCash = 0m,
        IsActive = true
    };

    private static TradeInstrument NewInstrument() => new()
    {
        InstrumentName = $"Test stock {Guid.NewGuid():N}",
        InstrumentSymbol = $"T{Guid.NewGuid():N}"[..18],
        IsActive = true
    };

    private static Position NewPosition(int accountId, int instrumentId,
        int quantity = 100, decimal averageCost = 200m) => new()
        {
            AccountId = accountId,
            InstrumentId = instrumentId,
            Quantity = quantity,
            ReservedQuantity = 0,
            AverageCost = averageCost
        };

    // Creates legitimate FK parents and records them for cleanup.
    private async Task<(Account account, TradeInstrument instrument)> SeedParentsAsync()
    {
        await using var db = CreateDbContext();
        var account = NewAccount();
        var instrument = NewInstrument();
        db.Accounts.Add(account);
        db.TradeInstruments.Add(instrument);
        await db.SaveChangesAsync();
        _accountIds.Add(account.AccountId);
        _instrumentIds.Add(instrument.InstrumentId);
        return (account, instrument);
    }

    private async Task<int> SeedPositionAsync(int accountId, int instrumentId,
        int quantity = 100, decimal averageCost = 200m)
    {
        await using var db = CreateDbContext();
        var repo = new PositionRepository(db);
        var position = NewPosition(accountId, instrumentId, quantity, averageCost);
        await repo.AddAsync(position);
        await Uow(db).SaveChangesAsync();
        return position.PositionId;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Clean up only this test's unique data, not the whole schema.
        await using var db = CreateDbContext();
        if (_accountIds.Count > 0)
        {
            await db.Positions
                .Where(p => _accountIds.Contains(p.AccountId))
                .ExecuteDeleteAsync();
        }
        if (_instrumentIds.Count > 0)
        {
            await db.TradeInstruments
                .Where(t => _instrumentIds.Contains(t.InstrumentId))
                .ExecuteDeleteAsync();
        }
        if (_accountIds.Count > 0)
        {
            await db.Accounts
                .Where(a => _accountIds.Contains(a.AccountId))
                .ExecuteDeleteAsync();
        }
    }

    // TEST 1: Add a position, save, and confirm it exists from a new DbContext.
    [Fact]
    public async Task AddPosition_SaveChanges_PersistsInOracle()
    {
        var (account, instrument) = await SeedParentsAsync();
        int positionId = await SeedPositionAsync(account.AccountId, instrument.InstrumentId);

        await using var verify = CreateDbContext();
        var actual = await verify.Positions.AsNoTracking()
            .SingleOrDefaultAsync(p => p.PositionId == positionId);

        Assert.NotNull(actual);
        Assert.Equal(account.AccountId, actual.AccountId);
        Assert.Equal(instrument.InstrumentId, actual.InstrumentId);
        Assert.Equal(100, actual.Quantity);
        Assert.Equal(0, actual.ReservedQuantity);
        Assert.Equal(200m, actual.AverageCost);
    }

    // TEST 2: Repository retrieves the correct position for account/instrument.
    [Fact]
    public async Task GetByAccountAndInstrument_Existing_ReturnsPosition()
    {
        var (account, instrument) = await SeedParentsAsync();
        int expectedId = await SeedPositionAsync(account.AccountId, instrument.InstrumentId);

        await using var db = CreateDbContext();
        var actual = await new PositionRepository(db)
            .GetByAccountAndInstrumentAsync(account.AccountId, instrument.InstrumentId);

        Assert.NotNull(actual);
        Assert.Equal(expectedId, actual.PositionId);
    }

    // TEST 3: Missing position returns null.
    [Fact]
    public async Task GetByAccountAndInstrument_NotFound_ReturnsNull()
    {
        var (account, instrument) = await SeedParentsAsync();
        await using var db = CreateDbContext();

        var actual = await new PositionRepository(db)
            .GetByAccountAndInstrumentAsync(account.AccountId, instrument.InstrumentId);

        Assert.Null(actual);
    }

    // TEST 4: Portfolio query returns only the requested account's positions.
    [Fact]
    public async Task GetByAccountId_ReturnsOnlyOwnPositions()
    {
        var (account1, instrument1) = await SeedParentsAsync();
        var (account2, instrument2) = await SeedParentsAsync();
        await SeedPositionAsync(account1.AccountId, instrument1.InstrumentId);
        await SeedPositionAsync(account1.AccountId, instrument2.InstrumentId);
        await SeedPositionAsync(account2.AccountId, instrument1.InstrumentId);

        await using var db = CreateDbContext();
        var positions = await new PositionRepository(db).GetByAccountIdAsync(account1.AccountId);

        Assert.Equal(2, positions.Count);
        Assert.All(positions, p => Assert.Equal(account1.AccountId, p.AccountId));
        Assert.Contains(positions, p => p.InstrumentId == instrument1.InstrumentId);
        Assert.Contains(positions, p => p.InstrumentId == instrument2.InstrumentId);
    }

    // TEST 5: EF change tracking persists quantity, reservation, and average cost.
    [Fact]
    public async Task ModifyTrackedPosition_SaveChanges_PersistsChanges()
    {
        var (account, instrument) = await SeedParentsAsync();
        int id = await SeedPositionAsync(account.AccountId, instrument.InstrumentId);

        await using (var db = CreateDbContext())
        {
            var position = await new PositionRepository(db)
                .GetByAccountAndInstrumentAsync(account.AccountId, instrument.InstrumentId);
            Assert.NotNull(position);
            position.Quantity = 150;
            position.ReservedQuantity = 20;
            position.AverageCost = 210m;
            await Uow(db).SaveChangesAsync();
        }

        await using var verify = CreateDbContext();
        var actual = await verify.Positions.AsNoTracking().SingleAsync(p => p.PositionId == id);
        Assert.Equal(150, actual.Quantity);
        Assert.Equal(20, actual.ReservedQuantity);
        Assert.Equal(130, actual.AvailableQuantity);
        Assert.Equal(210m, actual.AverageCost);
    }

    // TEST 6: Composite unique index rejects two positions for the same account/instrument.
    [Fact]
    public async Task AddPosition_DuplicateAccountInstrument_ThrowsDbUpdateException()
    {
        var (account, instrument) = await SeedParentsAsync();
        await SeedPositionAsync(account.AccountId, instrument.InstrumentId);

        await using (var db = CreateDbContext())
        {
            await new PositionRepository(db)
                .AddAsync(NewPosition(account.AccountId, instrument.InstrumentId));
            await Assert.ThrowsAsync<DbUpdateException>(() => Uow(db).SaveChangesAsync());
        }

        await using var verify = CreateDbContext();
        int count = await verify.Positions.AsNoTracking().CountAsync(
            p => p.AccountId == account.AccountId && p.InstrumentId == instrument.InstrumentId);
        Assert.Equal(1, count);
    }

    // TEST 7: FK constraint rejects a position referencing a nonexistent account.
    [Fact]
    public async Task AddPosition_NonexistentAccount_ThrowsDbUpdateException()
    {
        var (_, instrument) = await SeedParentsAsync();

        await using var db = CreateDbContext();
        var position = NewPosition(-12345, instrument.InstrumentId);
        await new PositionRepository(db).AddAsync(position);

        await Assert.ThrowsAsync<DbUpdateException>(() => Uow(db).SaveChangesAsync());
    }

    // TEST 8: Account and position updates commit together.
    [Fact]
    public async Task Transaction_AccountAndPosition_CommitTogether()
    {
        var (account, instrument) = await SeedParentsAsync();
        int id = await SeedPositionAsync(account.AccountId, instrument.InstrumentId);

        await using (var db = CreateDbContext())
        {
            var accounts = new AccountRepository(db);
            var positions = new PositionRepository(db);
            await Uow(db).ExecuteInTransactionAsync(async ct =>
            {
                var trackedAccount = await accounts.GetByIdAsync(account.AccountId, ct);
                var trackedPosition = await positions.GetByAccountAndInstrumentAsync(
                    account.AccountId, instrument.InstrumentId, ct);
                Assert.NotNull(trackedAccount);
                Assert.NotNull(trackedPosition);
                trackedAccount.CashBalance -= 1000m;
                trackedAccount.AvailableCash -= 1000m;
                trackedPosition.Quantity += 5;
                return true;
            });
        }

        await using var verify = CreateDbContext();
        var savedAccount = await verify.Accounts.AsNoTracking()
            .SingleAsync(a => a.AccountId == account.AccountId);
        var savedPosition = await verify.Positions.AsNoTracking()
            .SingleAsync(p => p.PositionId == id);
        Assert.Equal(9000m, savedAccount.CashBalance);
        Assert.Equal(9000m, savedAccount.AvailableCash);
        Assert.Equal(105, savedPosition.Quantity);
    }

    // TEST 9: SQL changes already sent to Oracle roll back together on failure.
    [Fact]
    public async Task Transaction_AccountAndPosition_RollbackTogether()
    {
        var (account, instrument) = await SeedParentsAsync();
        int id = await SeedPositionAsync(account.AccountId, instrument.InstrumentId);

        await using (var db = CreateDbContext())
        {
            var accounts = new AccountRepository(db);
            var positions = new PositionRepository(db);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Uow(db).ExecuteInTransactionAsync(async ct =>
                {
                    var trackedAccount = await accounts.GetByIdAsync(account.AccountId, ct);
                    var trackedPosition = await positions.GetByAccountAndInstrumentAsync(
                        account.AccountId, instrument.InstrumentId, ct);
                    Assert.NotNull(trackedAccount);
                    Assert.NotNull(trackedPosition);
                    trackedAccount.CashBalance -= 1000m;
                    trackedAccount.AvailableCash -= 1000m;
                    trackedPosition.Quantity += 5;

                    // Ensure Oracle has received BOTH UPDATEs before the exception.
                    await db.SaveChangesAsync(ct);
                    throw new InvalidOperationException("Simulated settlement failure");
#pragma warning disable CS0162
                    return true;
#pragma warning restore CS0162
                }));
        }

        await using var verify = CreateDbContext();
        var savedAccount = await verify.Accounts.AsNoTracking()
            .SingleAsync(a => a.AccountId == account.AccountId);
        var savedPosition = await verify.Positions.AsNoTracking()
            .SingleAsync(p => p.PositionId == id);
        Assert.Equal(10000m, savedAccount.CashBalance);
        Assert.Equal(10000m, savedAccount.AvailableCash);
        Assert.Equal(100, savedPosition.Quantity);
    }
}
