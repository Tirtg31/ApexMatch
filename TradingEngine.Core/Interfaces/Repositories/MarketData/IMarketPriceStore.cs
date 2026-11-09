using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TradingEngine.Core.Entities;

namespace TradingEngine.Core.Interfaces.Repositories.MarketData
{
    public interface IMarketPriceStore
    {
        bool TryGetPrice(string instrumentSymbol, out MarketPrice? price);
        void UpdatePrice(MarketPrice price);
        IReadOnlyCollection<MarketPrice> GetAllPrices();
    }
}
