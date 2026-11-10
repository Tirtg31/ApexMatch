using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Interfaces.Repositories.MarketData;

namespace TradingEngine.Infrastructure.MarketData
{
    public class MarketPriceStore : IMarketPriceStore
    {
        private readonly ConcurrentDictionary<string, MarketPrice> prices = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyCollection<MarketPrice> GetAllPrices()
        {
            return prices.Values.ToArray();
        }

        public bool TryGetPrice(string instrumentSymbol, out MarketPrice? price)
        {
            price = null;
            if (string.IsNullOrWhiteSpace(instrumentSymbol))
                return false;
            string normalizedInstrument = instrumentSymbol.Trim().ToUpperInvariant();
            return prices.TryGetValue(normalizedInstrument, out price);
        }

        public void UpdatePrice(MarketPrice price)
        {
            ArgumentNullException.ThrowIfNull(price);

            if (string.IsNullOrWhiteSpace(price.InstrumentSymbol))
                throw new ArgumentException("Instrument symbol is required.", nameof(price));

            if (price.Price <= 0)
                throw new ArgumentOutOfRangeException(nameof(price), "Market price must be positive.");

            string normalizedSymbol = price.InstrumentSymbol.Trim().ToUpperInvariant();
            var normalizedPrice = price with
            {
                InstrumentSymbol = normalizedSymbol
            };
            prices[normalizedSymbol] = normalizedPrice;
        }
    }
}
