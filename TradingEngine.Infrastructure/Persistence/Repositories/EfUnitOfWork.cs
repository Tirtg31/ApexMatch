using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TradingEngine.Core.Interfaces.Repositories;

namespace TradingEngine.Infrastructure.Persistence.Repositories
{
    public class EfUnitOfWork : IUnitOfWork
    {
        private readonly TradingDbContext db;
        private readonly ILogger<EfUnitOfWork> logger;
        public EfUnitOfWork(TradingDbContext db, ILogger<EfUnitOfWork> logger)
        {
            this.db = db;
            this.logger = logger;
        }
        public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct=default)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                T result = await work(ct);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return result;
            }
            catch
            {
                db.ChangeTracker.Clear();
                try
                {
                    await tx.RollbackAsync(CancellationToken.None);
                }
                catch(Exception rollbackEx)
                {
                    logger.LogError(rollbackEx, "Failed to rollback database transaction.");
                }
                throw;
            }
        }
        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            return db.SaveChangesAsync(ct);
        }
    }
}
