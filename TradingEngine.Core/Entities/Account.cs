using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TradingEngine.Core.Interfaces;

namespace TradingEngine.Core.Entities
{
    public class Account:IVersionedEntity
    {
        public int AccountId {  get; set; }
        public string AccountName { get; set; } = string.Empty;
        public decimal CashBalance {  get; set; }
        public decimal ReservedCash {  get; set; }
        public decimal AvailableCash {  get; set; }
        public bool IsActive {  get; set; }

        //Navigation Properties
        public ICollection<Order> Orders { get; set; } =new List<Order>();
        public ICollection<Position> Positions { get; set; } =new List<Position>();

        //Concurrency token
        public long Version { get; set; } = 1;
    }
}
