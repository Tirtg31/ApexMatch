using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Threading.Tasks;
using TradingEngine.Core.Entities;

namespace TradingEngine.Infrastructure.Persistence.Repositories
{
    public class InstrumentRepository : IInstrumentRepository
    {
        private readonly TradingDbContext db;
        public InstrumentRepository(TradingDbContext db)
        {
            this.db = db;
        }

        public async Task<List<TradeInstrument>> GetAllAsync(CancellationToken ct = default)
        {
            return await db.TradeInstruments.AsNoTracking().ToListAsync(ct);
        }

        public async Task<TradeInstrument?> GetByIdAsync(int instrumentId, CancellationToken ct = default)
        {
            return await db.TradeInstruments.AsNoTracking().FirstOrDefaultAsync(i => i.InstrumentId == instrumentId, ct);
        }
    }
}
