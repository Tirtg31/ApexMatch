using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Interfaces.Repositories;

namespace TradingEngine.Infrastructure.Persistence.Repositories
{
    public class AccountRepository : IAccountRepository
    {
        private readonly TradingDbContext db;
        public AccountRepository(TradingDbContext db)
        {
            this.db = db;
        }

        public async Task AddAsync(Account account, CancellationToken cancellationToken = default)
        {
            await db.Accounts.AddAsync(account, cancellationToken);
        }

        public async Task<Account?> GetByIdAsync(int accountId, CancellationToken cancellationToken = default)
        {
            var account = await db.Accounts.FirstOrDefaultAsync(x=>x.AccountId == accountId, cancellationToken);
            if (account == null) return null;
            return account;
        }
    }
}
