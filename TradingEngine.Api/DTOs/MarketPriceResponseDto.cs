namespace TradingEngine.Api.DTOs
{
    public class MarketPriceResponseDto
    {
        public int? InstrumentId { get; set; }
        public required string InstrumentSymbol { get; set; }
        public decimal Price { get; set; }
        public DateTimeOffset LastUpdatedUtc { get; set; }
        public required string Provider { get; set; }
    }
}
