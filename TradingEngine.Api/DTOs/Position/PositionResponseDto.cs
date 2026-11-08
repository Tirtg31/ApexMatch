namespace TradingEngine.Api.DTOs.Position
{
    public class PositionResponseDto
    {
        public int AccountId { get; set; }
        public int InstrumentId { get; set; }
        public int Quantity { get; set; }
        public int ReservedQuantity { get; set; }
        public int AvailableQuantity { get; set; }
        public decimal AverageCost { get; set; }
    }
}
