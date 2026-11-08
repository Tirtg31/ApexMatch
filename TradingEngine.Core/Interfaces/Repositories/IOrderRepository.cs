using TradingEngine.Core.Entities;

namespace TradingEngine.Core.Interfaces.Repositories
{
    public interface IOrderRepository
    {
        Task AddAsync(Order order, CancellationToken ct = default);
        Task<Order?> GetByIdAsync(int orderId, CancellationToken ct = default);
        Task<List<Order>> GetByAccountIdAsync(int accountId, CancellationToken ct = default);
        Task<List<Order>> GetOpenOrdersByInstrumentAsync(int instrumentId, CancellationToken ct = default);
        Task<List<Order>>GetAllOpenOrdersAsync(CancellationToken ct= default);
    }
}