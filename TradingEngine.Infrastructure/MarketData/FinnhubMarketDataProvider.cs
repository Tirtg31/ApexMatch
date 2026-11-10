using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Interfaces.Repositories.MarketData;
using TradingEngine.Infrastructure.MarketData.DTOs;

namespace TradingEngine.Infrastructure.MarketData
{
    public class FinnhubMarketDataProvider : IMarketDataProvider
    {
        private readonly HttpClient httpClient;
        private readonly ILogger<FinnhubMarketDataProvider> logger;
        public FinnhubMarketDataProvider(HttpClient httpClient, ILogger<FinnhubMarketDataProvider> logger)
        {
            this.httpClient = httpClient;
            this.logger = logger;
        }

        public async Task<MarketPrice?> GetCurrentPriceAsync(string instrumentSymbol, CancellationToken ct = default)
        {
            if(string.IsNullOrWhiteSpace(instrumentSymbol))
                throw new ArgumentException("Instrument symbol cannot be null or whitespace.", nameof(instrumentSymbol));

            instrumentSymbol = instrumentSymbol.Trim().ToUpperInvariant();
            string path=$"quote?symbol={Uri.EscapeDataString(instrumentSymbol)}";
            using var response=await httpClient.GetAsync(path, ct);
            response.EnsureSuccessStatusCode();

            var quote=await response.Content.ReadFromJsonAsync<FinnhubQuoteDto>(cancellationToken: ct);
            if (quote == null || quote.CurrentPrice <= 0 || quote.Timestamp <= 0)
            {
                logger.LogWarning("Finnhub returned an unavailable quote for {Symbol}", instrumentSymbol);
                return null;
            }
            DateTimeOffset timestamp;
            try
            {
                timestamp = DateTimeOffset.FromUnixTimeSeconds(quote.Timestamp);
            }
            catch (ArgumentOutOfRangeException)
            {
                logger.LogWarning("Finnhub returned an invalid timestamp for {Symbol}", instrumentSymbol);
                return null;
            }

            MarketPrice marketPrice=new MarketPrice
            {
                InstrumentSymbol=instrumentSymbol,
                Price=quote.CurrentPrice,
                LastUpdatedUtc= timestamp,
                Provider="Finnhub"
            };

            return marketPrice;
        }
    }
}
