using TradingEngine.Core.Entities;

namespace TradingEngine.Core.Interfaces.Repositories
{
    public interface IPositionRepository
    {
        Task<Position?> GetByAccountAndInstrumentAsync(int accountId, int instrumentId, CancellationToken ct = default);
        Task AddAsync(Position position, CancellationToken ct = default);
        Task<List<Position>> GetByAccountIdAsync(int accountId, CancellationToken ct = default);
    }
}