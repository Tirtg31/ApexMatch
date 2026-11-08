using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TradingEngine.Core.Interfaces;

namespace TradingEngine.Core.Entities
{
    public class Position:IVersionedEntity
    {
        public int PositionId { get; set; }
        public int AccountId {  get; set; }
        public int InstrumentId {  get; set; }
        public int Quantity {  get; set; }
        public int ReservedQuantity { get; set; }
        public int AvailableQuantity => Quantity - ReservedQuantity;
        public decimal AverageCost {  get; set; }

        //Navigation Properties
        public Account Account { get; set; } = null!;
        public TradeInstrument TradeInstrument { get; set; } = null!;
        //Concurrency token
        public long Version { get; set; } = 1;
    }
}
