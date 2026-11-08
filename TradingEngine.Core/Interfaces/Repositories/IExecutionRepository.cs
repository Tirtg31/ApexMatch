
using TradingEngine.Core.Entities;

namespace TradingEngine.Core.Interfaces.Repositories
{
    public interface IExecutionRepository
    {
        Task AddAsync(Execution execution, CancellationToken ct = default);
        Task<Execution?> GetByIdAsync(int executionId, CancellationToken ct = default);
        Task<List<Execution>> GetByOrderIdAsync(int orderId, CancellationToken ct = default);
    }
}
