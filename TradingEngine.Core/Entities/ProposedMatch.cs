using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TradingEngine.Core.Entities
{
    public sealed record ProposedMatch(int BuyOrderId, int SellOrderId, int InstrumentId, int Quantity, decimal Price);
}
