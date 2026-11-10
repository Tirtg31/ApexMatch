using Microsoft.AspNetCore.Mvc;
using System.Diagnostics.Metrics;
using TradingEngine.Api.DTOs;
using TradingEngine.Api.DTOs.Instrument;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Interfaces.Repositories.MarketData;
using TradingEngine.Infrastructure.MarketData;
using TradingEngine.Infrastructure.Persistence.Repositories;

namespace TradingEngine.Api.Controllers
{
    [ApiController]
    public class InstrumentController : ControllerBase
    {
        private readonly IInstrumentRepository instrumentRepository;
        private readonly IMarketPriceStore marketPriceStore;
        public InstrumentController(IInstrumentRepository instrumentRepository, IMarketPriceStore marketPriceStore)
        {
            this.instrumentRepository = instrumentRepository;
            this.marketPriceStore = marketPriceStore;
        }
        [HttpGet("instruments")]
        public async Task<ActionResult<List<InstrumentResponseDto>>> GetAllInstruments()
        {
            List<TradeInstrument>responseList= await instrumentRepository.GetAllAsync();
            List<InstrumentResponseDto> res = new List<InstrumentResponseDto>();
            foreach(var it in responseList)
            {
                InstrumentResponseDto temp = new InstrumentResponseDto
                {
                    InstrumentId=it.InstrumentId,
                    InstrumentName=it.InstrumentName,
                    InstrumentSymbol=it.InstrumentSymbol,
                    IsActive=it.IsActive
                };
                res.Add(temp);
            }
            return Ok(res);
        }
        [HttpGet("instruments/{id:int}")]
        public async Task<ActionResult<InstrumentResponseDto>> GetInstrument(int id, CancellationToken ct)
        {
            TradeInstrument? instrument=await instrumentRepository.GetByIdAsync(id,ct);
            if(instrument==null) return NotFound($"Instrument with ID {id} not found.");
            InstrumentResponseDto response = new InstrumentResponseDto
            {
                InstrumentId = instrument.InstrumentId,
                InstrumentName = instrument.InstrumentName,
                InstrumentSymbol = instrument.InstrumentSymbol,
                IsActive = instrument.IsActive
            };
            return Ok(response);
        }

        [HttpGet("{id:int}/price")]
        public async Task<ActionResult<MarketPriceResponseDto>> GetPrice(int id, CancellationToken ct)
        {
            if (id <= 0)
                return BadRequest("Instrument ID must be positive.");

            TradeInstrument? instrument = await instrumentRepository.GetByIdAsync(id, ct);

            if (instrument == null)
                return NotFound($"Instrument with ID {id} not found.");

            bool found = marketPriceStore.TryGetPrice(instrument.InstrumentSymbol,out MarketPrice? marketPrice);
            if (!found || marketPrice == null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, $"Market price for {instrument.InstrumentSymbol} is not available.");
            }

            MarketPriceResponseDto response = new MarketPriceResponseDto
            {
                InstrumentId = instrument.InstrumentId,
                InstrumentSymbol = marketPrice.InstrumentSymbol,
                Price = marketPrice.Price,
                LastUpdatedUtc = marketPrice.LastUpdatedUtc,
                Provider = marketPrice.Provider
            };
            return Ok(response);
        }
        [HttpGet("api/instruments/prices")]
        public ActionResult<IReadOnlyCollection<MarketPriceResponseDto>> GetAllPrices()
        {
            IReadOnlyCollection<MarketPrice> allPrices=marketPriceStore.GetAllPrices();
            IReadOnlyCollection<MarketPriceResponseDto> responeList = allPrices.Select(it => new MarketPriceResponseDto
            {
                InstrumentId = null,
                InstrumentSymbol = it.InstrumentSymbol,
                Price = it.Price,
                LastUpdatedUtc = it.LastUpdatedUtc,
                Provider = it.Provider
            }).ToArray();

            return Ok(responeList);
        }
    }
}
