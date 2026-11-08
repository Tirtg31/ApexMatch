using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Core.Entities;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradingEngine.Tests.Persistence;

/// <summary>
/// Oracle integration tests for Account.Version optimistic concurrency.
/// Requires the AddAccountVersion migration and an Oracle test database.
/// Set TRADING_ENGINE_TEST_CONNECTION to your TEST Oracle connection string.
/// Do not run against production: these tests insert/update/delete accounts.
/// </summary>
public sealed class AccountOptimisticConcurrencyIntegrationTests
{
    private static TradingDbContext CreateDbContext()
    {
        var connectionString = "User Id=trading;Password=trade_123;Data Source=localhost:1521/XEPDB1";
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "Set TRADING_ENGINE_TEST_CONNECTION to your Oracle test database connection string.");

        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseOracle(connectionString, oracle =>
                oracle.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion21))
            .Options;
        return new TradingDbContext(options);
    }

    private static Account NewAccount(string name, decimal cash = 10_000m) => new()
    {
        AccountName = name,
        CashBalance = cash,
        AvailableCash = cash,
        ReservedCash = 0m,
        IsActive = true
    };

    private static EfUnitOfWork Uow(TradingDbContext db) =>
        new(db, NullLogger<EfUnitOfWork>.Instance);

    private static async Task<int> SeedAsync(string name, decimal cash = 10_000m)
    {
        await using var db = CreateDbContext();
        var account = NewAccount(name, cash);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account.AccountId;
    }

    private static async Task<Account> ReadAsync(int accountId)
    {
        await using var db = CreateDbContext();
        return await db.Accounts.AsNoTracking()
            .SingleAsync(a => a.AccountId == accountId);
    }

    private static async Task CleanupAsync(params string[] names)
    {
        await using var db = CreateDbContext();
        await db.Accounts.Where(a => names.Contains(a.AccountName)).ExecuteDeleteAsync();
    }

    private static void AssertBalance(Account a, decimal cash, decimal reserved, decimal available, long version)
    {
        Assert.Equal(cash, a.CashBalance);
        Assert.Equal(reserved, a.ReservedCash);
        Assert.Equal(available, a.AvailableCash);
        Assert.Equal(cash, a.ReservedCash + a.AvailableCash);
        Assert.Equal(version, a.Version);
    }

    // 1. A new account must start at version 1.
    [Fact]
    public async Task NewAccount_ShouldPersistInitialVersionOne()
    {
        var name = $"OCC_New_{Guid.NewGuid():N}";
        try
        {
            var id = await SeedAsync(name);
            AssertBalance(await ReadAsync(id), 10_000m, 0m, 10_000m, 1L);
        }
        finally { await CleanupAsync(name); }
    }

    // 2. A tracked update should increase the version exactly once.
    [Fact]
    public async Task ModifiedAccount_ShouldIncrementVersionOnEverySave()
    {
        var name = $"OCC_Mod_{Guid.NewGuid():N}";
        try
        {
            var id = await SeedAsync(name);
            await using (var db = CreateDbContext())
            {
                var account = await db.Accounts.SingleAsync(a => a.AccountId == id);
                account.CashBalance += 200m;
                account.AvailableCash += 200m;
                await db.SaveChangesAsync();
                Assert.Equal(2L, account.Version);

                account.CashBalance += 300m;
                account.AvailableCash += 300m;
                await db.SaveChangesAsync();
                Assert.Equal(3L, account.Version);
            }
            AssertBalance(await ReadAsync(id), 10_500m, 0m, 10_500m, 3L);
        }
        finally { await CleanupAsync(name); }
    }

    // 3. An unchanged tracked account should not get a new version.
    [Fact]
    public async Task UnmodifiedAccount_ShouldNotIncrementVersion()
    {
        var name = $"OCC_Unchanged_{Guid.NewGuid():N}";
        try
        {
            var id = await SeedAsync(name);
            await using (var db = CreateDbContext())
            {
                _ = await db.Accounts.SingleAsync(a => a.AccountId == id);
                Assert.Equal(0, await db.SaveChangesAsync());
            }
            Assert.Equal(1L, (await ReadAsync(id)).Version);
        }
        finally { await CleanupAsync(name); }
    }

    // 4. The second DbContext must not overwrite a committed update based on an older version.
    [Fact]
    public async Task TwoContexts_StaleReservation_ShouldThrowAndPreserveFirstUpdate()
    {
        var name = $"OCC_Stale_{Guid.NewGuid():N}";
        try
        {
            var id = await SeedAsync(name);
            await using (var firstDb = CreateDbContext())
            await using (var secondDb = CreateDbContext())
            {
                var first = await firstDb.Accounts.SingleAsync(a => a.AccountId == id);
                var second = await secondDb.Accounts.SingleAsync(a => a.AccountId == id);
                Assert.Equal(first.Version, second.Version);

                first.AvailableCash -= 7_000m;
                first.ReservedCash += 7_000m;
                await firstDb.SaveChangesAsync();

                second.AvailableCash -= 7_000m;
                second.ReservedCash += 7_000m;
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                    () => secondDb.SaveChangesAsync());
            }
            AssertBalance(await ReadAsync(id), 10_000m, 7_000m, 3_000m, 2L);
        }
        finally { await CleanupAsync(name); }
    }

    // 5. Separate DbContexts may update DIFFERENT accounts successfully.
    [Fact]
    public async Task SeparateContexts_DifferentAccounts_ShouldBothSucceed()
    {
        var name1 = $"OCC_DifferentA_{Guid.NewGuid():N}";
        var name2 = $"OCC_DifferentB_{Guid.NewGuid():N}";
        try
        {
            var id1 = await SeedAsync(name1);
            var id2 = await SeedAsync(name2);
            await using (var db1 = CreateDbContext())
            await using (var db2 = CreateDbContext())
            {
                var a = await db1.Accounts.SingleAsync(x => x.AccountId == id1);
                var b = await db2.Accounts.SingleAsync(x => x.AccountId == id2);
                a.CashBalance += 100m;
                a.AvailableCash += 100m;
                b.CashBalance += 200m;
                b.AvailableCash += 200m;
                await db1.SaveChangesAsync();
                await db2.SaveChangesAsync();
            }
            AssertBalance(await ReadAsync(id1), 10_100m, 0m, 10_100m, 2L);
            AssertBalance(await ReadAsync(id2), 10_200m, 0m, 10_200m, 2L);
        }
        finally { await CleanupAsync(name1, name2); }
    }

    // 6. Reading the fresh version and rechecking funds must reject an unaffordable reservation.
    [Fact]
    public async Task AfterConflict_FreshRead_ShouldRejectInsufficientAvailableCash()
    {
        var name = $"OCC_Recheck_{Guid.NewGuid():N}";
        try
        {
            var id = await SeedAsync(name);
            await using (var db1 = CreateDbContext())
            await using (var db2 = CreateDbContext())
            {
                var first = await db1.Accounts.SingleAsync(x => x.AccountId == id);
                var stale = await db2.Accounts.SingleAsync(x => x.AccountId == id);
                first.AvailableCash -= 7_000m;
                first.ReservedCash += 7_000m;
                await db1.SaveChangesAsync();

                stale.AvailableCash -= 7_000m;
                stale.ReservedCash += 7_000m;
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                    () => db2.SaveChangesAsync());
            }

            // A real retry must start with fresh data, not the failed DbContext.
            await using (var retryDb = CreateDbContext())
            {
                var current = await retryDb.Accounts.SingleAsync(x => x.AccountId == id);
                Assert.Equal(2L, current.Version);
                Assert.True(current.AvailableCash < 7_000m,
                    "A second 7000 reservation must be rejected after reloading.");
                // No mutation and no save: the business rule rejects the retry.
            }
            AssertBalance(await ReadAsync(id), 10_000m, 7_000m, 3_000m, 2L);
        }
        finally { await CleanupAsync(name); }
    }

    // 7. A conflict inside UnitOfWork must roll back an earlier SaveChanges in the SAME transaction.
    [Fact]
    public async Task ConflictWithinUnitOfWork_ShouldRollbackEarlierDatabaseWrite()
    {
        var nameA = $"OCC_RollbackA_{Guid.NewGuid():N}";
        var nameB = $"OCC_RollbackB_{Guid.NewGuid():N}";
        try
        {
            var idA = await SeedAsync(nameA);
            var idB = await SeedAsync(nameB);

            await using (var staleDb = CreateDbContext())
            {
                // Load B BEFORE the concurrent writer commits, making this tracked copy stale.
                var staleB = await staleDb.Accounts.SingleAsync(x => x.AccountId == idB);
                await using (var otherDb = CreateDbContext())
                {
                    var freshB = await otherDb.Accounts.SingleAsync(x => x.AccountId == idB);
                    freshB.CashBalance += 500m;
                    freshB.AvailableCash += 500m;
                    await otherDb.SaveChangesAsync();
                }

                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
                    Uow(staleDb).ExecuteInTransactionAsync(async token =>
                    {
                        // First database write is executed but must NOT remain committed.
                        var a = await staleDb.Accounts.SingleAsync(x => x.AccountId == idA, token);
                        a.CashBalance += 1_000m;
                        a.AvailableCash += 1_000m;
                        await staleDb.SaveChangesAsync(token);

                        // Second write uses an outdated concurrency token.
                        staleB.CashBalance += 800m;
                        staleB.AvailableCash += 800m;
                        return true; // UoW attempts SaveChanges and encounters conflict.
                    }));
            }
            AssertBalance(await ReadAsync(idA), 10_000m, 0m, 10_000m, 1L);
            AssertBalance(await ReadAsync(idB), 10_500m, 0m, 10_500m, 2L);
        }
        finally { await CleanupAsync(nameA, nameB); }
    }

    // 8. A normal UnitOfWork transaction still commits account updates and increments the token.
    [Fact]
    public async Task UnitOfWork_SuccessfulUpdate_ShouldCommitAndIncrementVersion()
    {
        var name = $"OCC_Uow_{Guid.NewGuid():N}";
        try
        {
            var id = await SeedAsync(name);
            await using (var db = CreateDbContext())
            {
                var result = await Uow(db).ExecuteInTransactionAsync(async token =>
                {
                    var a = await db.Accounts.SingleAsync(x => x.AccountId == id, token);
                    a.AvailableCash -= 1_500m;
                    a.ReservedCash += 1_500m;
                    return a.AccountId;
                });
                Assert.Equal(id, result);
            }
            AssertBalance(await ReadAsync(id), 10_000m, 1_500m, 8_500m, 2L);
        }
        finally { await CleanupAsync(name); }
    }

    // 9. A rejected transaction must not modify balances or the version.
    [Fact]
    public async Task UnitOfWork_BusinessRuleFailure_ShouldNotIncrementVersion()
    {
        var name = $"OCC_Reject_{Guid.NewGuid():N}";
        try
        {
            var id = await SeedAsync(name);
            await using (var db = CreateDbContext())
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    Uow(db).ExecuteInTransactionAsync(async token =>
                    {
                        var a = await db.Accounts.SingleAsync(x => x.AccountId == id, token);
                        if (a.AvailableCash < 12_000m)
                            throw new InvalidOperationException("Insufficient funds");
                        a.AvailableCash -= 12_000m;
                        a.ReservedCash += 12_000m;
                        return true;
                    }));
            }
            AssertBalance(await ReadAsync(id), 10_000m, 0m, 10_000m, 1L);
        }
        finally { await CleanupAsync(name); }
    }
}
