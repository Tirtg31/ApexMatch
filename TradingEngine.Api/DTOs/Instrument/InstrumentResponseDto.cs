namespace TradingEngine.Api.DTOs.Instrument
{
    public class InstrumentResponseDto
    {
        public int InstrumentId { get; set; }
        public string InstrumentName { get; set; } = string.Empty;
        public string InstrumentSymbol { get; set; } = string.Empty;
        public bool IsActive { get; set; }
    }
}
