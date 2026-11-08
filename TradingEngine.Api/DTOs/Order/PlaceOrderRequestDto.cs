
using System.ComponentModel.DataAnnotations;
using TradingEngine.Core.Enums;

namespace TradingEngine.Api.DTOs.Order
{
    public class PlaceOrderRequestDto
    {
        [Range(1, int.MaxValue)]
        public int AccountId { get; set; }

        [Range(1, int.MaxValue)]
        public int InstrumentId { get; set; }

        [EnumDataType(typeof(OrderSide))]
        public OrderSide Side { get; set; }

        [Range(1, int.MaxValue)]
        public int Quantity { get; set; }

        [Range(typeof(decimal), "0.01", "79228162514264337593543950335")]
        public decimal LimitPrice { get; set; }
    }
}
