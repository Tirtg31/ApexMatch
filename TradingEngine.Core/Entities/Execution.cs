using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TradingEngine.Core.Entities
{
    public class Execution
    {
        public int ExecutionId { get; set; }
        public int BuyOrderId {  get; set; }
        public int SellOrderId {  get; set; }
        public int ExecutedQuantity {  get; set; }
        public decimal ExecutedPrice {  get; set; }
        public DateTime ExecutedAt { get; set; }
        public Order BuyOrder { get; set; } = null!;
        public Order SellOrder { get; set; }= null!;
    }
}
