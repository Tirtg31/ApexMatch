
using System;
using Xunit;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Services;

namespace TradingEngine.Tests
{
    public class AccountServiceTests
    {
        private readonly AccountService accountService = new();

        private Account CreateAccount(decimal balance = 50000m)
        {
            return accountService.CreateAccount(new Account
            {
                AccountName = "Test Account",
                CashBalance = balance
            });
        }

        private static void AssertBalances(
            Account account,
            decimal cash,
            decimal available,
            decimal reserved)
        {
            Assert.Equal(cash, account.CashBalance);
            Assert.Equal(available, account.AvailableCash);
            Assert.Equal(reserved, account.ReservedCash);

            Assert.Equal(
                account.CashBalance,
                account.AvailableCash + account.ReservedCash);
        }
        [Fact]
        public void GetAccountById_ShouldNotExposeInternalAccount()
        {
            var service = new AccountService();

            var created = service.CreateAccount(new Account
            {
                AccountName = "Trader",
                CashBalance = 10000
            });

            var fetched = service.GetAccountById(created.AccountId)!;

            fetched.AvailableCash = 999999;

            var actual = service.GetAccountById(created.AccountId)!;

            Assert.Equal(10000m, actual.AvailableCash);
        }

        [Fact]
        public void CreateAccount_ShouldInitializeBalancesCorrectly()
        {
            Account account = CreateAccount();

            Assert.Equal(1, account.AccountId);
            Assert.True(account.IsActive);

            AssertBalances(account, 50000m, 50000m, 0m);
        }

        [Fact]
        public void CreateMultipleAccounts_ShouldGenerateUniqueIds()
        {
            Account first = CreateAccount();
            Account second = CreateAccount();

            // Verify unique account IDs
            Assert.NotEqual(first.AccountId, second.AccountId);

            Account? fetchedFirst =
                accountService.GetAccountById(first.AccountId);

            Account? fetchedSecond =
                accountService.GetAccountById(second.AccountId);

            Assert.NotNull(fetchedFirst);
            Assert.NotNull(fetchedSecond);

            // Verify correct accounts were retrieved
            Assert.Equal(first.AccountId, fetchedFirst.AccountId);
            Assert.Equal(second.AccountId, fetchedSecond.AccountId);

            // Verify account information
            Assert.Equal(first.AccountName, fetchedFirst.AccountName);
            Assert.Equal(first.CashBalance, fetchedFirst.CashBalance);

            Assert.Equal(second.AccountName, fetchedSecond.AccountName);
            Assert.Equal(second.CashBalance, fetchedSecond.CashBalance);

            // Verify defensive copying
            Assert.NotSame(first, fetchedFirst);
            Assert.NotSame(second, fetchedSecond);
        }

        [Theory]
        [InlineData(-100)]
        [InlineData(-1)]
        public void CreateAccount_WithNegativeBalance_ShouldThrow(
            int balance)
        {
            Assert.Throws<ArgumentException>(() =>
                CreateAccount(balance));
        }

        [Fact]
        public void CreateAccount_WithEmptyName_ShouldThrow()
        {
            Assert.Throws<ArgumentException>(() =>
                accountService.CreateAccount(new Account
                {
                    AccountName = "",
                    CashBalance = 1000m
                }));
        }

        [Fact]
        public void GetAccountById_UnknownAccount_ShouldReturnNull()
        {
            Assert.Null(accountService.GetAccountById(999));
        }

        [Fact]
        public void DepositFunds_ShouldIncreaseCashAndAvailableBalance()
        {
            Account account = CreateAccount(10000m);

            accountService.DepositFunds(account.AccountId, 5000m);

            AssertBalances(account, 15000m, 15000m, 0m);
        }

        [Fact]
        public void WithdrawFunds_ShouldDecreaseCashAndAvailableBalance()
        {
            Account account = CreateAccount(10000m);

            accountService.WithdrawFunds(account.AccountId, 3000m);

            AssertBalances(account, 7000m, 7000m, 0m);
        }

        [Fact]
        public void WithdrawFunds_ExceedingAvailableCash_ShouldThrow()
        {
            Account account = CreateAccount(10000m);

            accountService.ReserveFunds(account.AccountId, 8000m);

            Assert.Throws<ArgumentException>(() =>
                accountService.WithdrawFunds(account.AccountId, 3000m));

            AssertBalances(account, 10000m, 2000m, 8000m);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-100)]
        public void DepositFunds_InvalidAmount_ShouldThrow(int amount)
        {
            Account account = CreateAccount();

            Assert.Throws<ArgumentException>(() =>
                accountService.DepositFunds(account.AccountId, amount));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-100)]
        public void WithdrawFunds_InvalidAmount_ShouldThrow(int amount)
        {
            Account account = CreateAccount();

            Assert.Throws<ArgumentException>(() =>
                accountService.WithdrawFunds(account.AccountId, amount));
        }

        [Fact]
        public void InactiveAccount_ShouldRejectDepositWithdrawalAndReservation()
        {
            Account account = CreateAccount(10000m);
            account.IsActive = false;

            Assert.Throws<ArgumentException>(() =>
                accountService.DepositFunds(account.AccountId, 100m));

            Assert.Throws<ArgumentException>(() =>
                accountService.WithdrawFunds(account.AccountId, 100m));

            Assert.Throws<InvalidOperationException>(() =>
                accountService.ReserveFunds(account.AccountId, 100m));

            AssertBalances(account, 10000m, 10000m, 0m);
        }

        [Fact]
        public void ReserveFunds_ShouldMoveAvailableCashToReservedCash()
        {
            Account account = CreateAccount(50000m);

            accountService.ReserveFunds(account.AccountId, 20000m);

            AssertBalances(account, 50000m, 30000m, 20000m);
        }

        [Fact]
        public void ReserveFunds_InsufficientCash_ShouldThrow()
        {
            Account account = CreateAccount(10000m);

            Assert.Throws<InvalidOperationException>(() =>
                accountService.ReserveFunds(account.AccountId, 15000m));

            AssertBalances(account, 10000m, 10000m, 0m);
        }

        [Fact]
        public void MultipleReservations_ShouldAccumulate()
        {
            Account account = CreateAccount(50000m);

            accountService.ReserveFunds(account.AccountId, 5000m);
            accountService.ReserveFunds(account.AccountId, 20000m);
            accountService.ReserveFunds(account.AccountId, 5000m);

            AssertBalances(account, 50000m, 20000m, 30000m);
        }

        [Fact]
        public void ReleaseFunds_ShouldRestoreAvailableCash()
        {
            Account account = CreateAccount(50000m);

            accountService.ReserveFunds(account.AccountId, 20000m);
            accountService.ReleaseFunds(account.AccountId, 12000m);

            AssertBalances(account, 50000m, 42000m, 8000m);
        }

        [Fact]
        public void ReleaseFunds_ExceedingReservedCash_ShouldThrow()
        {
            Account account = CreateAccount(10000m);

            accountService.ReserveFunds(account.AccountId, 3000m);

            Assert.Throws<InvalidOperationException>(() =>
                accountService.ReleaseFunds(account.AccountId, 5000m));

            AssertBalances(account, 10000m, 7000m, 3000m);
        }

        [Fact]
        public void SettleBuyer_ShouldDeductActualCostAndReleasePriceDifference()
        {
            Account account = CreateAccount(50000m);

            accountService.ReserveFunds(account.AccountId, 20000m);

            // 40 shares reserved at 200, executed at 195
            accountService.SettleBuyer(
                executionId: 1,
                accountId: account.AccountId,
                reservedAmount: 8000m,
                actualCost: 7800m);

            AssertBalances(account, 42200m, 30200m, 12000m);
        }

        [Fact]
        public void SettleBuyer_MultipleExecutions_ShouldCalculateCorrectBalances()
        {
            Account account = CreateAccount(50000m);

            accountService.ReserveFunds(account.AccountId, 20000m);

            // First 40 shares execute at 195
            accountService.SettleBuyer(1, account.AccountId, 8000m, 7800m);

            // Remaining 60 shares execute at 198
            accountService.SettleBuyer(2, account.AccountId, 12000m, 11880m);

            AssertBalances(account, 30320m, 30320m, 0m);
        }

        [Fact]
        public void SettleBuyer_DuplicateExecution_ShouldThrow()
        {
            Account account = CreateAccount(50000m);

            accountService.ReserveFunds(account.AccountId, 20000m);

            accountService.SettleBuyer(1, account.AccountId, 8000m, 7800m);

            Assert.Throws<InvalidOperationException>(() =>
                accountService.SettleBuyer(
                    1, account.AccountId, 8000m, 7800m));

            AssertBalances(account, 42200m, 30200m, 12000m);
        }

        [Fact]
        public void SettleBuyer_ActualCostExceedsReservation_ShouldThrow()
        {
            Account account = CreateAccount(50000m);

            accountService.ReserveFunds(account.AccountId, 8000m);

            Assert.Throws<ArgumentException>(() =>
                accountService.SettleBuyer(
                    1, account.AccountId, 8000m, 8500m));

            AssertBalances(account, 50000m, 42000m, 8000m);
        }

        [Fact]
        public void SettleSeller_ShouldCreditExecutionProceeds()
        {
            Account account = CreateAccount(10000m);

            accountService.SettleSeller(1, account.AccountId, 7800m);

            AssertBalances(account, 17800m, 17800m, 0m);
        }

        [Fact]
        public void SettleSeller_DuplicateExecution_ShouldThrow()
        {
            Account account = CreateAccount(10000m);

            accountService.SettleSeller(1, account.AccountId, 7800m);

            Assert.Throws<InvalidOperationException>(() =>
                accountService.SettleSeller(
                    1, account.AccountId, 7800m));

            AssertBalances(account, 17800m, 17800m, 0m);
        }

        [Fact]
        public void SettleSeller_MultipleExecutions_ShouldAccumulateProceeds()
        {
            Account account = CreateAccount(10000m);

            accountService.SettleSeller(1, account.AccountId, 7800m);
            accountService.SettleSeller(2, account.AccountId, 11880m);

            AssertBalances(account, 29680m, 29680m, 0m);
        }

        [Fact]
        public void CancelRemainingReservation_AfterPartialSettlement()
        {
            Account account = CreateAccount(50000m);

            accountService.ReserveFunds(account.AccountId, 20000m);

            accountService.SettleBuyer(
                1, account.AccountId, 8000m, 7800m);

            // Cancel remaining 60 shares at 200
            accountService.ReleaseFunds(account.AccountId, 12000m);

            AssertBalances(account, 42200m, 42200m, 0m);
        }
    }
}
