using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Interfaces.Repositories;

namespace TradingEngine.Infrastructure.Persistence.Repositories
{
    public class OrderRepository : IOrderRepository
    {
        private readonly TradingDbContext db;
        public OrderRepository(TradingDbContext db)
        {
            this.db = db;
        }

        public async Task AddAsync(Order order, CancellationToken ct = default)
        {
            await db.Orders.AddAsync(order, ct);
        }

        public async Task<List<Order>> GetByAccountIdAsync(int accountId, CancellationToken ct = default)
        {
            return await db.Orders.Where(o=>o.AccountId==accountId).ToListAsync(ct);
        }

        public async Task<Order?> GetByIdAsync(int orderId, CancellationToken ct = default)
        {
            return await db.Orders.FirstOrDefaultAsync(o => o.OrderId == orderId, ct);
        }

        public async Task<List<Order>> GetOpenOrdersByInstrumentAsync(int instrumentId, CancellationToken ct = default)
        {
            return await db.Orders.Where(o=>o.InstrumentId==instrumentId&&(o.Status==OrderStatus.Pending|| o.Status == OrderStatus.PartiallyFilled)).OrderBy(o => o.CreatedAt).ThenBy(o => o.OrderId).ToListAsync(ct);
        }
        public async Task<List<Order>>GetAllOpenOrdersAsync(CancellationToken ct=default)
        {
            return await db.Orders.AsNoTracking()
                                  .Where(o=> o.Status == OrderStatus.Pending || o.Status == OrderStatus.PartiallyFilled).OrderBy(o => o.CreatedAt).ThenBy(o => o.OrderId).ToListAsync(ct);
        }
    }
}
