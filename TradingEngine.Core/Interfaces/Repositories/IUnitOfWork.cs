using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TradingEngine.Core.Interfaces.Repositories
{
    public interface IUnitOfWork
    {
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken,Task<T>>work, CancellationToken ct=default);
        Task<int> SaveChangesAsync(CancellationToken ct = default);
    }
}
