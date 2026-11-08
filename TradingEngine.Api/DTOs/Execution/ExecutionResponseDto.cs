namespace TradingEngine.Api.DTOs.Execution
{
    public class ExecutionResponseDto
    {
        public int ExecutionId { get; set; }
        public int BuyOrderId { get; set; }
        public int SellOrderId { get; set; }
        public int ExecutedQuantity { get; set; }
        public decimal ExecutedPrice { get; set; }
        public DateTime ExecutedAt { get; set; }
    }
}
