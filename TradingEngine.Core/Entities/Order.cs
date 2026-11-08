using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Interfaces;
namespace TradingEngine.Core.Entities
{
    public class Order:IVersionedEntity
    {
        public int OrderId { get; set; }
        public int AccountId {  get; set; }
        public int InstrumentId {  get; set; }
        public OrderSide Side { get; set; }
        public OrderType OrderType {  get; set; }
        public int Quantity {  get; set; }
        public decimal? LimitPrice {  get; set; }
        public int FilledQuantity {  get; set; }
        public OrderStatus Status { get; set; }

        //Navigation Properties
        public Account Account { get; set; } = null!;
        public TradeInstrument TradeInstrument { get; set; } = null!;
        public DateTime CreatedAt {  get; set; }
        public ICollection<Execution> BuyExecutions { get; set; } = new List<Execution>();
        public ICollection<Execution> SellExecutions { get; set; } = new List<Execution>();

        //Concurrency token
        public long Version { get; set; } = 1;
    }
}
