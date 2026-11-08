
using TradingEngine.Api.DTOs.Execution;

namespace TradingEngine.Api.DTOs.Trading
{
    public class MatchResultDto
    {
        public int InstrumentId { get; set; }
        public int TotalExecutions { get; set; }
        public int TotalExecutedQuantity { get; set; }
        public decimal TotalTradedValue { get; set; }
        public List<ExecutionResponseDto> Executions { get; set; } = new();
    }
}
