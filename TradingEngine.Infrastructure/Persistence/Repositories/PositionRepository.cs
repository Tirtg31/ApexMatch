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
    public class PositionRepository : IPositionRepository
    {
        private readonly TradingDbContext db;
        public PositionRepository(TradingDbContext db)
        {
            this.db = db;
        }

        public async Task AddAsync(Position position, CancellationToken ct = default)
        {
            await db.Positions.AddAsync(position, ct);
        }

        public async Task<Position?> GetByAccountAndInstrumentAsync(int accountId, int instrumentId, CancellationToken ct = default)
        {
            return await db.Positions.FirstOrDefaultAsync(p => p.AccountId == accountId && p.InstrumentId == instrumentId, ct);
        }

        public async Task<List<Position>> GetByAccountIdAsync(int accountId, CancellationToken ct = default)
        {
            return await db.Positions.Where(p => p.AccountId == accountId).ToListAsync(ct);
        }
    }
}
