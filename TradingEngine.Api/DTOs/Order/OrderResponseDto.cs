
using TradingEngine.Core.Enums;

namespace TradingEngine.Api.DTOs.Order
{
    public class OrderResponseDto
    {
        public int OrderId { get; set; }
        public int AccountId { get; set; }
        public int InstrumentId { get; set; }
        public OrderSide Side { get; set; }
        public OrderType OrderType { get; set; }
        public int Quantity { get; set; }
        public decimal? LimitPrice { get; set; }
        public int FilledQuantity { get; set; }
        public int RemainingQuantity { get; set; }
        public OrderStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
