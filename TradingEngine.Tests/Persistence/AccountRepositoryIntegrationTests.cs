
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Core.Entities;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradingEngine.Tests.Persistence
{
    public class EfUnitOfWorkIntegrationTests
    {
        private TradingDbContext CreateDbContext()
        {
            string connectionString =
                "User Id=trading;Password=trade_123;Data Source=localhost:1521/XEPDB1";

            var options = new DbContextOptionsBuilder<TradingDbContext>()
                .UseOracle(
                    connectionString,
                    oracleOptions =>
                        oracleOptions.UseOracleSQLCompatibility(
                            OracleSQLCompatibility.DatabaseVersion21))
                .Options;

            return new TradingDbContext(options);
        }

        private static Account CreateAccount(string name)
        {
            return new Account
            {
                AccountName = name,
                CashBalance = 10000m,
                AvailableCash = 10000m,
                ReservedCash = 0m,
                IsActive = true
            };
        }

        private static EfUnitOfWork CreateUnitOfWork(
            TradingDbContext db)
        {
            return new EfUnitOfWork(
                db,
                NullLogger<EfUnitOfWork>.Instance);
        }

        private async Task CleanupAsync(params string[] names)
        {
            await using var db = CreateDbContext();

            await db.Accounts
                .Where(a => names.Contains(a.AccountName))
                .ExecuteDeleteAsync();
        }
        // TEST 1
        // Verify that AccountRepository.AddAsync()
        // followed by UnitOfWork.SaveChangesAsync()
        // persists the account in the Oracle database.
        [Fact]
        public async Task AddAccount_SaveChanges_ShouldPersistInDatabase()
        {
            // Arrange
            int accountId;

            string accountName =
                $"TestAccount_{Guid.NewGuid():N}";

            await using (var db = CreateDbContext())
            {
                var repository = new AccountRepository(db);

                var unitOfWork = new EfUnitOfWork(
                    db,
                    NullLogger<EfUnitOfWork>.Instance);

                var account = new Account
                {
                    AccountName = accountName,
                    CashBalance = 10000m,
                    AvailableCash = 10000m,
                    ReservedCash = 0m,
                    IsActive = true
                };

                // Act
                await repository.AddAsync(account);
                await unitOfWork.SaveChangesAsync();

                accountId = account.AccountId;
            }

            // Assert
            // Use a completely new DbContext to verify
            // that the account was actually saved in Oracle.
            await using var verificationDb = CreateDbContext();

            var savedAccount = await verificationDb.Accounts
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    a => a.AccountId == accountId);

            Assert.NotNull(savedAccount);

            Assert.Equal(accountName, savedAccount.AccountName);
            Assert.Equal(10000m, savedAccount.CashBalance);
            Assert.Equal(10000m, savedAccount.AvailableCash);
            Assert.Equal(0m, savedAccount.ReservedCash);
            Assert.True(savedAccount.IsActive);
        }

        // TEST 2
        // Verify that a successful transaction commits
        // both account insertions to the Oracle database.
        [Fact]
        public async Task Transaction_Success_ShouldCommitBothAccounts()
        {
            // Arrange
            string name1 = $"Buyer_{Guid.NewGuid():N}";
            string name2 = $"Seller_{Guid.NewGuid():N}";

            try
            {
                await using (var db = CreateDbContext())
                {
                    var repo = new AccountRepository(db);
                    var unitOfWork = CreateUnitOfWork(db);

                    // Act
                    bool result =
                        await unitOfWork.ExecuteInTransactionAsync(
                            async ct =>
                            {
                                await repo.AddAsync(
                                    CreateAccount(name1), ct);

                                await repo.AddAsync(
                                    CreateAccount(name2), ct);

                                return true;
                            });

                    Assert.True(result);
                }

                // Assert using a fresh DbContext
                await using var verificationDb = CreateDbContext();

                var accounts = await verificationDb.Accounts
                    .AsNoTracking()
                    .Where(a =>
                        a.AccountName == name1 ||
                        a.AccountName == name2)
                    .ToListAsync();

                Assert.Equal(2, accounts.Count);
                Assert.Contains(accounts, a => a.AccountName == name1);
                Assert.Contains(accounts, a => a.AccountName == name2);
            }
            finally
            {
                await CleanupAsync(name1, name2);
            }
        }

        // TEST 3
        // Verify that an exception before SaveChangesAsync
        // prevents any accounts from being persisted.
        [Fact]
        public async Task Transaction_ExceptionBeforeSave_ShouldPersistNothing()
        {
            // Arrange
            string name1 = $"Buyer_{Guid.NewGuid():N}";
            string name2 = $"Seller_{Guid.NewGuid():N}";

            try
            {
                await using (var db = CreateDbContext())
                {
                    var repo = new AccountRepository(db);
                    var unitOfWork = CreateUnitOfWork(db);

                    // Act & Assert
                    await Assert.ThrowsAsync<InvalidOperationException>(
                        () => unitOfWork.ExecuteInTransactionAsync(
                            async ct =>
                            {
                                await repo.AddAsync(
                                    CreateAccount(name1), ct);

                                await repo.AddAsync(
                                    CreateAccount(name2), ct);

                                throw new InvalidOperationException(
                                    "Simulated transaction failure");

#pragma warning disable CS0162
                                return true;
#pragma warning restore CS0162
                            }));
                }

                // Verify neither account was persisted
                await using var verificationDb = CreateDbContext();

                int count = await verificationDb.Accounts
                    .AsNoTracking()
                    .CountAsync(a =>
                        a.AccountName == name1 ||
                        a.AccountName == name2);

                Assert.Equal(0, count);
            }
            finally
            {
                await CleanupAsync(name1, name2);
            }
        }

        // TEST 4
        // Verify that Oracle rolls back an INSERT that
        // has already executed through SaveChangesAsync,
        // but has not yet been committed.
        [Fact]
        public async Task Transaction_ExceptionAfterSave_ShouldRollbackDatabaseChanges()
        {
            // Arrange
            string accountName = $"Rollback_{Guid.NewGuid():N}";

            try
            {
                await using (var db = CreateDbContext())
                {
                    var repo = new AccountRepository(db);
                    var unitOfWork = CreateUnitOfWork(db);

                    // Act & Assert
                    await Assert.ThrowsAsync<InvalidOperationException>(
                        () => unitOfWork.ExecuteInTransactionAsync(
                            async ct =>
                            {
                                await repo.AddAsync(
                                    CreateAccount(accountName), ct);

                                // Execute INSERT inside the transaction
                                await db.SaveChangesAsync(ct);

                                // Simulate settlement failure
                                throw new InvalidOperationException(
                                    "Simulated settlement failure");

#pragma warning disable CS0162
                                return true;
#pragma warning restore CS0162
                            }));
                }

                // Assert using a NEW DbContext
                await using var verificationDb = CreateDbContext();

                var account = await verificationDb.Accounts
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        a => a.AccountName == accountName);

                Assert.Null(account);
            }
            finally
            {
                await CleanupAsync(accountName);
            }
        }

        // TEST 5
        // Verify that when one account violates a
        // database constraint, no account from the
        // same transaction is committed.
        [Fact]
        public async Task Transaction_InvalidDatabaseOperation_ShouldRollbackEverything()
        {
            // Arrange
            string validName = $"Valid_{Guid.NewGuid():N}";

            // AccountName has a maximum length of 100.
            string invalidName = new string('X', 150);

            try
            {
                await using (var db = CreateDbContext())
                {
                    var repo = new AccountRepository(db);
                    var unitOfWork = CreateUnitOfWork(db);

                    // Act
                    await Assert.ThrowsAnyAsync<Exception>(
                        () => unitOfWork.ExecuteInTransactionAsync(
                            async ct =>
                            {
                                await repo.AddAsync(
                                    CreateAccount(validName), ct);

                                // Persist valid account inside transaction
                                await db.SaveChangesAsync(ct);

                                // Add invalid account
                                await repo.AddAsync(
                                    CreateAccount(invalidName), ct);

                                // UnitOfWork's final SaveChangesAsync
                                // should fail before commit.
                                return true;
                            }));
                }

                // Assert
                await using var verificationDb = CreateDbContext();

                var validAccount = await verificationDb.Accounts
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        a => a.AccountName == validName);

                Assert.Null(validAccount);
            }
            finally
            {
                await CleanupAsync(validName);
            }
        }

        // TEST 6
        // Verify that changes to an existing account are
        // rolled back if the transaction fails after SaveChangesAsync.
        [Fact]
        public async Task Transaction_UpdateExistingAccount_ShouldRollbackOnFailure()
        {
            // Arrange
            string name = $"UpdateRollback_{Guid.NewGuid():N}";

            try
            {
                int accountId;

                // First create a committed account.
                await using (var db = CreateDbContext())
                {
                    var repo = new AccountRepository(db);
                    var uow = CreateUnitOfWork(db);

                    var account = CreateAccount(name);
                    await repo.AddAsync(account);
                    await uow.SaveChangesAsync();

                    accountId = account.AccountId;
                }

                // Act
                await using (var db = CreateDbContext())
                {
                    var repo = new AccountRepository(db);
                    var uow = CreateUnitOfWork(db);

                    await Assert.ThrowsAsync<InvalidOperationException>(
                        () => uow.ExecuteInTransactionAsync(
                            async ct =>
                            {
                                var account =
                                    await repo.GetByIdAsync(accountId, ct);

                                Assert.NotNull(account);

                                account.CashBalance -= 3000m;
                                account.AvailableCash -= 3000m;

                                // UPDATE is executed but not committed.
                                await db.SaveChangesAsync(ct);

                                throw new InvalidOperationException(
                                    "Simulated settlement failure");

#pragma warning disable CS0162
                                return true;
#pragma warning restore CS0162
                            }));
                }

                // Assert using fresh DbContext.
                await using var verificationDb = CreateDbContext();

                var saved = await verificationDb.Accounts
                    .AsNoTracking()
                    .SingleAsync(a => a.AccountId == accountId);

                Assert.Equal(10000m, saved.CashBalance);
                Assert.Equal(10000m, saved.AvailableCash);
            }
            finally
            {
                await CleanupAsync(name);
            }
        }

        // TEST 7
        // Verify that a successful transaction returns
        // the value produced by its work delegate.
        [Fact]
        public async Task Transaction_Success_ShouldReturnResult()
        {
            // Arrange
            string name = $"Result_{Guid.NewGuid():N}";

            try
            {
                int returnedAccountId;

                // Act
                await using (var db = CreateDbContext())
                {
                    var repo = new AccountRepository(db);
                    var uow = CreateUnitOfWork(db);

                    returnedAccountId =
                        await uow.ExecuteInTransactionAsync(
                            async ct =>
                            {
                                var account = CreateAccount(name);

                                await repo.AddAsync(account, ct);

                                // Oracle generates the identity during save.
                                await db.SaveChangesAsync(ct);

                                return account.AccountId;
                            });
                }

                // Assert
                Assert.True(returnedAccountId > 0);

                await using var verificationDb = CreateDbContext();

                var saved = await verificationDb.Accounts
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        a => a.AccountId == returnedAccountId);

                Assert.NotNull(saved);
                Assert.Equal(name, saved.AccountName);
            }
            finally
            {
                await CleanupAsync(name);
            }
        }


        // TEST 8
        // Verify that an already-cancelled token prevents
        // the transaction from executing any work.
        [Fact]
        public async Task Transaction_CancelledToken_ShouldNotExecuteWork()
        {
            // Arrange
            await using var db = CreateDbContext();
            var uow = CreateUnitOfWork(db);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            bool workExecuted = false;

            // Act & Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => uow.ExecuteInTransactionAsync(
                    ct =>
                    {
                        workExecuted = true;
                        return Task.FromResult(true);
                    },
                    cts.Token));

            Assert.False(workExecuted);
        }



    }
}
