using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TradingEngine.Core.Entities;

namespace TradingEngine.Core.Interfaces.Repositories.MarketData
{
    public interface IMarketDataProvider
    {
        Task<MarketPrice?> GetCurrentPriceAsync(string instrumentSymbol, CancellationToken ct = default);
    }
}
