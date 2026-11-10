using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TradingEngine.Core.Entities
{
    public record MarketPrice
    {
        public required string InstrumentSymbol { get; init; }
        public decimal Price { get; init; }
        public DateTimeOffset LastUpdatedUtc { get; init; }
        public required string Provider { get; init; }
    }
}
