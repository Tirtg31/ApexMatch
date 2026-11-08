using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TradingEngine.Core.Entities
{
    public class TradeInstrument
    {
        public int InstrumentId {  get; set; }
        public string InstrumentName { get; set; } = string.Empty;
        public string InstrumentSymbol {  get; set; }=string.Empty;
        public bool IsActive {  get; set; }

        //Navigation Properties
        public ICollection<Order> Orders { get; set; } = new List<Order>();
        public ICollection<Position> Positions { get; set; } = new List<Position>();
    }
}
