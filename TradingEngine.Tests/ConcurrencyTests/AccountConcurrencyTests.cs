using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Services;
using Xunit;

namespace TradingEngine.Tests.ConcurrencyTests;

public class AccountConcurrencyTests
{
    private static (AccountService Service, int AccountId) CreateAccount(decimal balance)
    {
        var service = new AccountService();
        var account = service.CreateAccount(new Account
        {
            AccountName = "Concurrent Test Account",
            CashBalance = balance
        });
        return (service, account.AccountId);
    }

    // Two orders compete to reserve more cash than the account can cover together.
    [Fact]
    public async Task ConcurrentReservations_ShouldNotOverspend()
    {
        var (service, id) = CreateAccount(10_000m);
        using var start = new ManualResetEventSlim(false);
        using var ready = new CountdownEvent(2);

        Task<bool> Reserve() => Task.Run(() =>
        {
            ready.Signal();
            start.Wait();
            try
            {
                service.ReserveFunds(id, 7_000m);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        });

        Task<bool> first = Reserve();
        Task<bool> second = Reserve();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "Worker tasks did not start in time.");
        start.Set();
        bool[] results = await Task.WhenAll(first, second);

        var account = service.GetAccountById(id)!;
        Assert.Single(results.Where(success => success));
        Assert.Equal(10_000m, account.CashBalance);
        Assert.Equal(3_000m, account.AvailableCash);
        Assert.Equal(7_000m, account.ReservedCash);
    }

    // Only 10 of 100 reservations of 1,000 can be accepted with 10,000 total cash.
    [Fact]
    public async Task HundredConcurrentReservations_ShouldAcceptExactlyTen()
    {
        var (service, id) = CreateAccount(10_000m);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool>[] tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                service.ReserveFunds(id, 1_000m);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        })).ToArray();

        start.SetResult(true);
        bool[] results = await Task.WhenAll(tasks);
        var account = service.GetAccountById(id)!;

        Assert.Equal(10, results.Count(success => success));
        Assert.Equal(10_000m, account.CashBalance);
        Assert.Equal(0m, account.AvailableCash);
        Assert.Equal(10_000m, account.ReservedCash);
    }

    // Every deposit must be reflected in both cash and available cash.
    [Fact]
    public async Task ConcurrentDeposits_ShouldNotLoseUpdates()
    {
        var (service, id) = CreateAccount(0m);
        Task[] tasks = Enumerable.Range(0, 1000)
            .Select(_ => Task.Run(() => service.DepositFunds(id, 10m)))
            .ToArray();

        await Task.WhenAll(tasks);
        var account = service.GetAccountById(id)!;

        Assert.Equal(10_000m, account.CashBalance);
        Assert.Equal(10_000m, account.AvailableCash);
        Assert.Equal(0m, account.ReservedCash);
    }

    // Only 10 withdrawals of 1,000 may succeed from a 10,000 account.
    [Fact]
    public async Task ConcurrentWithdrawals_ShouldNotOverdrawAccount()
    {
        var (service, id) = CreateAccount(10_000m);
        Task<bool>[] tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            try
            {
                service.WithdrawFunds(id, 1_000m);
                return true;
            }
            catch (ArgumentException ex) when (ex.Message.Contains("Insufficient funds"))
            {
                return false;
            }
        })).ToArray();

        bool[] results = await Task.WhenAll(tasks);
        var account = service.GetAccountById(id)!;

        Assert.Equal(10, results.Count(success => success));
        Assert.Equal(0m, account.CashBalance);
        Assert.Equal(0m, account.AvailableCash);
        Assert.Equal(0m, account.ReservedCash);
    }

    // Account IDs must remain unique when accounts are created in parallel.
    [Fact]
    public async Task ConcurrentAccountCreation_ShouldGenerateUniqueIds()
    {
        var service = new AccountService();
        Task<Account>[] tasks = Enumerable.Range(0, 100).Select(i => Task.Run(() =>
            service.CreateAccount(new Account
            {
                AccountName = $"Account {i}",
                CashBalance = 100m
            }))).ToArray();

        Account[] accounts = await Task.WhenAll(tasks);
        Assert.Equal(100, accounts.Length);
        Assert.Equal(100, accounts.Select(a => a.AccountId).Distinct().Count());
        Assert.All(accounts, a => Assert.NotNull(service.GetAccountById(a.AccountId)));
    }

    // A seller execution must only credit the seller once.
    [Fact]
    public async Task ConcurrentSellerSettlement_ShouldCreditExactlyOnce()
    {
        var (service, id) = CreateAccount(0m);
        Task<bool>[] tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            try
            {
                service.SettleSeller(101, id, 500m);
                return true;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already been settled"))
            {
                return false;
            }
        })).ToArray();

        bool[] results = await Task.WhenAll(tasks);
        var account = service.GetAccountById(id)!;

        Assert.Single(results.Where(success => success));
        Assert.Equal(500m, account.CashBalance);
        Assert.Equal(500m, account.AvailableCash);
    }

    // A buyer execution must only debit reserved cash once.
    [Fact]
    public async Task ConcurrentBuyerSettlement_ShouldDebitExactlyOnce()
    {
        var (service, id) = CreateAccount(1_000m);
        service.ReserveFunds(id, 600m);

        Task<bool>[] tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            try
            {
                service.SettleBuyer(202, id, 600m, 500m);
                return true;
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("already been settled") ||
                ex.Message.Contains("Insufficient reserved funds"))
            {
                return false;
            }
        })).ToArray();

        bool[] results = await Task.WhenAll(tasks);
        var account = service.GetAccountById(id)!;

        Assert.Single(results.Where(success => success));
        Assert.Equal(500m, account.CashBalance);
        Assert.Equal(500m, account.AvailableCash);
        Assert.Equal(0m, account.ReservedCash);
    }

}
