
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Services;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradingEngine.Tests.Persistence;

public class AccountServiceIntegrationTests
{
    private TradingDbContext CreateDbContext()
    {
        // Use the same test database as your existing
        // AccountRepositoryIntegrationTests.
        string connectionString =
            "User Id=trading;Password=trade_123;Data Source=localhost:1521/XEPDB1";

        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseOracle(connectionString, oracleOptions =>
                oracleOptions.UseOracleSQLCompatibility(
                    OracleSQLCompatibility.DatabaseVersion21))
            .Options;

        return new TradingDbContext(options);
    }

    private AccountService CreateService(TradingDbContext db)
    {
        var repo = new AccountRepository(db);

        var uow = new EfUnitOfWork(
            db,
            NullLogger<EfUnitOfWork>.Instance);

        return new AccountService(repo, uow);
    }

    private static Account NewAccount(string name, decimal balance = 10000m)
    {
        return new Account
        {
            AccountName = name,
            CashBalance = balance
        };
    }

    private async Task CleanupAsync(string name)
    {
        await using var db = CreateDbContext();

        await db.Accounts
            .Where(a => a.AccountName == name)
            .ExecuteDeleteAsync();
    }

    private async Task<Account> ReadSavedAsync(int accountId)
    {
        await using var db = CreateDbContext();

        return await db.Accounts
            .AsNoTracking()
            .SingleAsync(a => a.AccountId == accountId);
    }

    private static void AssertBalances(
        Account account,
        decimal cash,
        decimal reserved,
        decimal available)
    {
        Assert.Equal(cash, account.CashBalance);
        Assert.Equal(reserved, account.ReservedCash);
        Assert.Equal(available, account.AvailableCash);

        // Verify the balance invariant.
        Assert.Equal(
            account.CashBalance,
            account.AvailableCash + account.ReservedCash);
    }

    // TEST 1
    // Verify CreateAccountAsync persists a new account
    // with the correct initial balances.
    [Fact]
    public async Task CreateAccountAsync_ValidAccount_ShouldPersist()
    {
        string name = $"Create_{Guid.NewGuid():N}";

        try
        {
            int accountId;

            await using (var db = CreateDbContext())
            {
                var service = CreateService(db);

                var account = await service.CreateAccountAsync(
                    NewAccount(name), CancellationToken.None);

                accountId = account.AccountId;
            }

            var saved = await ReadSavedAsync(accountId);

            Assert.True(saved.AccountId > 0);
            Assert.Equal(name, saved.AccountName);
            Assert.True(saved.IsActive);
            AssertBalances(saved, 10000m, 0m, 10000m);
        }
        finally
        {
            await CleanupAsync(name);
        }
    }

    // TEST 2
    // Verify negative initial balances are rejected.
    [Fact]
    public async Task CreateAccountAsync_NegativeBalance_ShouldThrow()
    {
        string name = $"Invalid_{Guid.NewGuid():N}";

        await using var db = CreateDbContext();
        var service = CreateService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAccountAsync(
                NewAccount(name, -100m), CancellationToken.None));

        Assert.False(await db.Accounts
            .AnyAsync(a => a.AccountName == name));
    }

    // TEST 3
    // Verify an existing account can be retrieved.
    [Fact]
    public async Task GetAccountByIdAsync_ExistingAccount_ShouldReturnAccount()
    {
        string name = $"Get_{Guid.NewGuid():N}";

        try
        {
            await using var db = CreateDbContext();
            var service = CreateService(db);

            var created = await service.CreateAccountAsync(
                NewAccount(name), CancellationToken.None);

            var result = await service.GetAccountByIdAsync(
                created.AccountId, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(created.AccountId, result.AccountId);
            Assert.Equal(name, result.AccountName);
            AssertBalances(result, 10000m, 0m, 10000m);
        }
        finally
        {
            await CleanupAsync(name);
        }
    }

    // TEST 4
    // Verify an unknown account ID returns null.
    [Fact]
    public async Task GetAccountByIdAsync_UnknownAccount_ShouldReturnNull()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var result = await service.GetAccountByIdAsync(
            -1, CancellationToken.None);

        Assert.Null(result);
    }

    // TEST 5
    // Verify a deposit increases both CashBalance
    // and AvailableCash and persists the updates.
    [Fact]
    public async Task DepositFundsAsync_ValidAmount_ShouldIncreaseBalances()
    {
        string name = $"Deposit_{Guid.NewGuid():N}";

        try
        {
            int accountId;

            await using (var db = CreateDbContext())
            {
                var service = CreateService(db);

                var account = await service.CreateAccountAsync(
                    NewAccount(name), CancellationToken.None);

                accountId = account.AccountId;

                await service.DepositFundsAsync(
                    accountId, 2500m, CancellationToken.None);
            }

            var saved = await ReadSavedAsync(accountId);

            AssertBalances(saved, 12500m, 0m, 12500m);
        }
        finally
        {
            await CleanupAsync(name);
        }
    }

    // TEST 6
    // Verify a withdrawal reduces CashBalance
    // and AvailableCash.
    [Fact]
    public async Task WithdrawFundsAsync_ValidAmount_ShouldDecreaseBalances()
    {
        string name = $"Withdraw_{Guid.NewGuid():N}";

        try
        {
            int accountId;

            await using (var db = CreateDbContext())
            {
                var service = CreateService(db);

                var account = await service.CreateAccountAsync(
                    NewAccount(name), CancellationToken.None);

                accountId = account.AccountId;

                await service.WithdrawFundsAsync(
                    accountId, 3000m, CancellationToken.None);
            }

            var saved = await ReadSavedAsync(accountId);

            AssertBalances(saved, 7000m, 0m, 7000m);
        }
        finally
        {
            await CleanupAsync(name);
        }
    }

    // TEST 7
    // Verify withdrawal cannot exceed available funds.
    [Fact]
    public async Task WithdrawFundsAsync_InsufficientFunds_ShouldNotChangeBalances()
    {
        string name = $"OverWithdraw_{Guid.NewGuid():N}";

        try
        {
            int accountId;

            await using (var db = CreateDbContext())
            {
                var service = CreateService(db);

                var account = await service.CreateAccountAsync(
                    NewAccount(name), CancellationToken.None);

                accountId = account.AccountId;

                await Assert.ThrowsAsync<ArgumentException>(() =>
                    service.WithdrawFundsAsync(
                        accountId, 15000m, CancellationToken.None));
            }

            var saved = await ReadSavedAsync(accountId);

            AssertBalances(saved, 10000m, 0m, 10000m);
        }
        finally
        {
            await CleanupAsync(name);
        }
    }

    // TEST 8
    // Verify reserving funds transfers money from
    // AvailableCash to ReservedCash without changing CashBalance.
    [Fact]
    public async Task ReserveFundsAsync_ValidAmount_ShouldReserveCash()
    {
        string name = $"Reserve_{Guid.NewGuid():N}";

        try
        {
            int accountId;

            await using (var db = CreateDbContext())
            {
                var service = CreateService(db);

                var account = await service.CreateAccountAsync(
                    NewAccount(name), CancellationToken.None);

                accountId = account.AccountId;

                await service.ReserveFundsAsync(
                    accountId, 4000m, CancellationToken.None);
            }

            var saved = await ReadSavedAsync(accountId);

            AssertBalances(saved, 10000m, 4000m, 6000m);
        }
        finally
        {
            await CleanupAsync(name);
        }
    }

    // TEST 9
    // Verify an excessive reservation is rejected
    // and an earlier reservation remains unchanged.
    [Fact]
    public async Task ReserveFundsAsync_InsufficientFunds_ShouldNotOverReserve()
    {
        string name = $"OverReserve_{Guid.NewGuid():N}";

        try
        {
            int accountId;

            await using (var db = CreateDbContext())
            {
                var service = CreateService(db);

                var account = await service.CreateAccountAsync(
                    NewAccount(name), CancellationToken.None);

                accountId = account.AccountId;

                await service.ReserveFundsAsync(
                    accountId, 7000m, CancellationToken.None);

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.ReserveFundsAsync(
                        accountId, 4000m, CancellationToken.None));
            }

            var saved = await ReadSavedAsync(accountId);

            AssertBalances(saved, 10000m, 7000m, 3000m);
        }
        finally
        {
            await CleanupAsync(name);
        }
    }

    // TEST 10
    // Verify released funds return to AvailableCash.
    [Fact]
    public async Task ReleaseFundsAsync_ValidAmount_ShouldReleaseCash()
    {
        string name = $"Release_{Guid.NewGuid():N}";

        try
        {
            int accountId;

            await using (var db = CreateDbContext())
            {
                var service = CreateService(db);

                var account = await service.CreateAccountAsync(
                    NewAccount(name), CancellationToken.None);

                accountId = account.AccountId;

                await service.ReserveFundsAsync(
                    accountId, 6000m, CancellationToken.None);

                await service.ReleaseFundsAsync(
                    accountId, 2500m, CancellationToken.None);
            }

            var saved = await ReadSavedAsync(accountId);

            AssertBalances(saved, 10000m, 3500m, 6500m);
        }
        finally
        {
            await CleanupAsync(name);
        }
    }

    // TEST 11
    // Verify releasing more funds than reserved fails.
    [Fact]
    public async Task ReleaseFundsAsync_ExcessiveAmount_ShouldNotChangeBalances()
    {
        string name = $"OverRelease_{Guid.NewGuid():N}";

        try
        {
            int accountId;

            await using (var db = CreateDbContext())
            {
                var service = CreateService(db);

                var account = await service.CreateAccountAsync(
                    NewAccount(name), CancellationToken.None);

                accountId = account.AccountId;

                await service.ReserveFundsAsync(
                    accountId, 2000m, CancellationToken.None);

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.ReleaseFundsAsync(
                        accountId, 3000m, CancellationToken.None));
            }

            var saved = await ReadSavedAsync(accountId);

            AssertBalances(saved, 10000m, 2000m, 8000m);
        }
        finally
        {
            await CleanupAsync(name);
        }
    }

    // TEST 12
    // Verify an inactive account cannot receive deposits.
    [Fact]
    public async Task DepositFundsAsync_InactiveAccount_ShouldThrow()
    {
        string name = $"Inactive_{Guid.NewGuid():N}";

        try
        {
            int accountId;

            await using (var db = CreateDbContext())
            {
                var service = CreateService(db);

                var account = await service.CreateAccountAsync(
                    NewAccount(name), CancellationToken.None);

                accountId = account.AccountId;

                account.IsActive = false;
                await db.SaveChangesAsync();

                await Assert.ThrowsAsync<ArgumentException>(() =>
                    service.DepositFundsAsync(
                        accountId, 500m, CancellationToken.None));
            }

            var saved = await ReadSavedAsync(accountId);

            Assert.False(saved.IsActive);
            AssertBalances(saved, 10000m, 0m, 10000m);
        }
        finally
        {
            await CleanupAsync(name);
        }
    }
}
