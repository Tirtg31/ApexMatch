using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TradingEngine.Infrastructure.MarketData.DTOs
{
    public sealed class FinnhubQuoteDto
    {
        [JsonPropertyName("c")]
        public decimal CurrentPrice { get; set; }
        [JsonPropertyName("d")]
        public decimal Change { get; set; }
        [JsonPropertyName("dp")]
        public decimal PercentageChange { get; set; }
        [JsonPropertyName("h")]
        public decimal HighPrice { get; set; }
        [JsonPropertyName("l")]
        public decimal LowPrice { get; set; }
        [JsonPropertyName("o")]
        public decimal OpenPrice { get; set; }
        [JsonPropertyName("pc")]
        public decimal PreviousClosePrice { get; set; }
        [JsonPropertyName("t")]
        public long Timestamp { get; set; }
    }
}
