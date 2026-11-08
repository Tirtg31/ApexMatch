using TradingEngine.Core.Entities;

namespace TradingEngine.Core.Interfaces.Repositories
{
    public interface IAccountRepository
    {
        Task<Account?> GetByIdAsync(int accountId, CancellationToken cancellationToken = default);
        Task AddAsync(Account account, CancellationToken cancellationToken = default);
    }
}