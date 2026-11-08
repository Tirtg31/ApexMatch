using Microsoft.Identity.Client;
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Interfaces.Repositories;

namespace TradingEngine.Core.Services
{
    public class AccountService
    {
        private readonly Dictionary<int, Account> Accounts=new();
        private readonly HashSet<int> settledBuyExecutions = new();
        private readonly HashSet<int> settledSellExecutions = new();
        private readonly object accountLock = new(); 
        private int _nextAccountId = 1;

        private readonly IAccountRepository accountRepository;
        private readonly IUnitOfWork unitOfWork;
        public AccountService()
        {
            // Used by existing in-memory unit tests
        }
        public AccountService(IAccountRepository accountRepository, IUnitOfWork unitOfWork)
        {
            this.accountRepository = accountRepository;
            this.unitOfWork = unitOfWork;
        }

        private static Account CloneAccount(Account account)
        {
            return new Account
            {
                AccountId = account.AccountId,
                AccountName = account.AccountName,
                CashBalance = account.CashBalance,
                AvailableCash = account.AvailableCash,
                ReservedCash = account.ReservedCash,
                IsActive = account.IsActive
            };
        }
        public Account CreateAccount(Account account)
        {
            lock (accountLock)
            {
                if (account == null)
                    throw new ArgumentNullException(nameof(account));
                if (string.IsNullOrWhiteSpace(account.AccountName))
                    throw new ArgumentException("Account name is required");
                if (account.CashBalance < 0)
                    throw new ArgumentException("Cash must be positive");

                Account newAccount = new Account
                {
                    AccountId = _nextAccountId++,
                    AccountName = account.AccountName,
                    CashBalance = account.CashBalance,
                    ReservedCash = 0,
                    AvailableCash = account.CashBalance,
                    IsActive = true
                };
                Accounts.Add(newAccount.AccountId, newAccount);
                return newAccount;
            }    
        }
        public Account? GetAccountById(int accountId)
        {
            lock (accountLock)
            {
                if (!Accounts.TryGetValue(accountId, out Account? account))
                    return null;
                return CloneAccount(account);
            }
        }
        public void DepositFunds(int accountId, decimal amount)
        {
            lock (accountLock)
            {
                if (!Accounts.TryGetValue(accountId, out Account account))
                    throw new ArgumentException("Account not found");
                if (amount <= 0)
                    throw new ArgumentException("Amount Deposited must be greater than 0");
                if(!account.IsActive)
                    throw new ArgumentException("Account is inactive");
            
                account.CashBalance += amount;
                account.AvailableCash += amount;
            }
        }
        public void WithdrawFunds(int accountId, decimal amount)
        {
            lock (accountLock)
            {
                if (!Accounts.TryGetValue(accountId, out Account account))
                    throw new ArgumentException("Account not found");
                if (amount <= 0)
                    throw new ArgumentException("Amount Withdrawn must be greater than 0");
                if (amount > account.AvailableCash)
                    throw new ArgumentException("Insufficient funds");
                if (!account.IsActive)
                    throw new ArgumentException("Account is inactive");
            
                account.CashBalance -= amount;
                account.AvailableCash -= amount;
            }
        }
        public void ReserveFunds(int accountId, decimal amount)
        {
            if (amount <= 0)
                throw new ArgumentException("Amount must be positive");
            lock (accountLock)
            {
                if (!Accounts.TryGetValue(accountId, out Account? account))
                    throw new ArgumentException("Account not found");

                if (!account.IsActive)
                    throw new InvalidOperationException("Account is inactive");

                if (account.AvailableCash < amount)
                    throw new InvalidOperationException("Insufficient funds");
            
                account.AvailableCash -= amount;
                account.ReservedCash += amount;
            }
        }
        public void ReleaseFunds(int accountId, decimal amount)
        {
            lock (accountLock)
            {
                if (!Accounts.TryGetValue(accountId, out Account? account))
                    throw new ArgumentException("Account not found");

                if (amount <= 0)
                    throw new ArgumentException("Amount must be positive");

                if (account.ReservedCash < amount)
                    throw new InvalidOperationException("Insufficient reserved funds");
            
                account.ReservedCash -= amount;
                account.AvailableCash += amount;
            }
        }

        public void SettleBuyer(int executionId, int accountId, decimal reservedAmount, decimal actualCost)
        {
            lock (accountLock)
            {
                if (!Accounts.TryGetValue(accountId, out Account? account))
                    throw new ArgumentException("Account not found");
                if (reservedAmount <= 0 || actualCost <= 0 || actualCost > reservedAmount)
                    throw new ArgumentException("Invalid settlement amounts");
                if (account.ReservedCash < reservedAmount)
                    throw new InvalidOperationException("Insufficient reserved funds");
                if (settledBuyExecutions.Contains(executionId))
                    throw new InvalidOperationException("Buyer execution has already been settled");
                if(executionId<=0)
                    throw new ArgumentException("Invalid Execution Id");
            
                account.ReservedCash -= reservedAmount;
                account.CashBalance -= actualCost;
                account.AvailableCash += reservedAmount - actualCost;
                settledBuyExecutions.Add(executionId);
            }
        }
        public async Task SettleBuyerWithoutCommitAsync(int accountId, decimal reservedAmount, decimal actualCost, CancellationToken ct = default)
        {
            Account account = await GetAccountByIdAsync(accountId, ct)
                ?? throw new ArgumentException("Account not found");

            if (!account.IsActive)
                throw new InvalidOperationException("Account is inactive");

            if (reservedAmount <= 0 ||
                actualCost <= 0 ||
                actualCost > reservedAmount)
                throw new ArgumentException("Invalid settlement amounts");

            if (account.ReservedCash < reservedAmount ||
                account.CashBalance < actualCost)
                throw new InvalidOperationException("Insufficient funds");

            account.ReservedCash -= reservedAmount;
            account.CashBalance -= actualCost;
            account.AvailableCash += reservedAmount - actualCost;
        }
        public void SettleSeller(int executionId, int accountId, decimal proceeds)
        {
            lock (accountLock)
            {
                if (!Accounts.TryGetValue(accountId, out Account? account))
                    throw new ArgumentException("Account not found");
                if (proceeds <= 0)
                    throw new ArgumentException("Proceeds must be positive");
                if (settledSellExecutions.Contains(executionId))
                    throw new InvalidOperationException("Seller execution has already been settled");
                if (executionId <= 0)
                    throw new ArgumentException("Invalid Execution Id");
            
                account.CashBalance += proceeds;
                account.AvailableCash += proceeds;
                settledSellExecutions.Add(executionId);
            }
        }
        public async Task SettleSellerWithoutCommitAsync(int accountId, decimal proceeds, CancellationToken ct = default)
        {
            if (proceeds <= 0)
                throw new ArgumentException("Invalid proceeds");

            Account account = await GetAccountByIdAsync(accountId, ct)
                ?? throw new ArgumentException("Account not found");

            if (!account.IsActive)
                throw new InvalidOperationException("Account is inactive");

            account.CashBalance = checked(account.CashBalance + proceeds);
            account.AvailableCash = checked(account.AvailableCash + proceeds);
        }
        public async Task<Account> CreateAccountAsync(Account account, CancellationToken ct)
        {
            if (account == null)
                throw new ArgumentNullException(nameof(account));
            if (string.IsNullOrWhiteSpace(account.AccountName))
                throw new ArgumentException("Account name is required");
            if (account.CashBalance < 0)
                throw new ArgumentException("Cash must be positive");

            Account newAccount = new Account
            {
                AccountName = account.AccountName,
                CashBalance = account.CashBalance,
                ReservedCash = 0,
                AvailableCash = account.CashBalance,
                IsActive = true
            };
            await accountRepository.AddAsync(newAccount, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return newAccount;
        }
        public async Task<Account?> GetAccountByIdAsync(int accountId, CancellationToken ct)
        {
            return await accountRepository.GetByIdAsync(accountId, ct);
        }
        public async Task DepositFundsAsync(int accountId, decimal amount, CancellationToken ct)
        {
            if (amount <= 0)
                throw new ArgumentException("Amount Deposited must be greater than 0");

            await unitOfWork.ExecuteInTransactionAsync(async token =>
            {
                Account? account = await GetAccountByIdAsync(accountId, token);
                if (account == null)
                    throw new ArgumentException("Account not found");
                if (!account.IsActive)
                    throw new ArgumentException("Account is inactive");
                account.CashBalance += amount;
                account.AvailableCash += amount;
                return true;
            }, ct);
        }
        public async Task ReserveFundsAsync(int accountId, decimal amount, CancellationToken ct)
        {
            if (amount <= 0)
                throw new ArgumentException("Amount must be positive");
            await unitOfWork.ExecuteInTransactionAsync(async token =>
            {
                Account? account=await GetAccountByIdAsync(accountId, token);
                if(account == null)
                    throw new ArgumentException("Account not found");
                if (!account.IsActive)
                    throw new InvalidOperationException("Account is inactive");
                if (account.AvailableCash < amount)
                    throw new InvalidOperationException("Insufficient funds");
                account.AvailableCash -= amount;
                account.ReservedCash += amount;
                return true;
            },ct);
        }
        public async Task ReserveFundsWithoutCommitAsync(int accountId, decimal amount, CancellationToken ct=default)
        {
            if (amount <= 0)
                throw new ArgumentException("Amount must be positive");
            Account? account = await GetAccountByIdAsync(accountId, ct);
            if (account == null)
                throw new ArgumentException("Account not found");
            if (!account.IsActive)
                throw new InvalidOperationException("Account is inactive");
            if (account.AvailableCash < amount)
                throw new InvalidOperationException("Insufficient funds");
            account.AvailableCash -= amount;
            account.ReservedCash += amount;
        }
        public async Task WithdrawFundsAsync(int accountId, decimal amount, CancellationToken ct)
        {
            if (amount <= 0)
                throw new ArgumentException("Amount must be positive");
            await unitOfWork.ExecuteInTransactionAsync(async token =>
            {
                Account? account = await GetAccountByIdAsync(accountId, token);
                if (account == null)
                    throw new ArgumentException("Account not found");
                if (amount > account.AvailableCash)
                    throw new ArgumentException("Insufficient funds");
                if (!account.IsActive)
                    throw new ArgumentException("Account is inactive");
                account.CashBalance -= amount;
                account.AvailableCash -= amount;
                return true;
            }, ct);
        }
        public async Task ReleaseFundsAsync(int accountId, decimal amount, CancellationToken ct)
        {
            if (amount <= 0)
                throw new ArgumentException("Amount must be positive");
            await unitOfWork.ExecuteInTransactionAsync(async token =>
            {
                Account? account = await GetAccountByIdAsync(accountId, token);
                if (account == null)
                    throw new ArgumentException("Account not found");
                if (account.ReservedCash < amount)
                    throw new InvalidOperationException("Insufficient reserved funds");

                account.ReservedCash -= amount;
                account.AvailableCash += amount;
                return true;
            }, ct);
        }
        public async Task ReleaseFundsWithoutCommitAsync(int accountId, decimal amount, CancellationToken ct = default)
        {
            if (amount <= 0)
                throw new ArgumentException("Amount must be positive");
            Account? account = await GetAccountByIdAsync(accountId, ct);
            if (account == null)
                throw new ArgumentException("Account not found");
            if (!account.IsActive)
                throw new InvalidOperationException("Account is inactive");
            if (account.ReservedCash < amount)
                throw new InvalidOperationException("Insufficient funds");
            account.ReservedCash -= amount;
            account.AvailableCash += amount;
        }

    }
}

