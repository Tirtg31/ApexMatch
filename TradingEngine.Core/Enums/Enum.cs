using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TradingEngine.Core.Enums
{
    public enum OrderSide
    {
        Buy,
        Sell
    }
    public enum OrderType
    {
        Market,
        Limit
    }
    public enum OrderStatus
    {
        Pending,
        PartiallyFilled,
        Filled,
        Cancelled,
        Rejected
    }
}
