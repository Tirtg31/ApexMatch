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
    public class ExecutionRepository : IExecutionRepository
    {
        private readonly TradingDbContext db;
        public ExecutionRepository(TradingDbContext db)
        {
            this.db = db;
        }

        public async Task AddAsync(Execution execution, CancellationToken ct = default)
        {
            await db.Executions.AddAsync(execution, ct);
        }

        public async Task<Execution?> GetByIdAsync(int executionId, CancellationToken ct = default)
        {
            return await db.Executions.FirstOrDefaultAsync(e => e.ExecutionId==executionId, ct);
        }

        public async Task<List<Execution>> GetByOrderIdAsync(int orderId, CancellationToken ct = default)
        {
            return await db.Executions.Where(e => e.BuyOrderId == orderId || e.SellOrderId == orderId).OrderBy(e => e.ExecutedAt).ThenBy(e => e.ExecutionId).ToListAsync(ct);
        }
    }
}
