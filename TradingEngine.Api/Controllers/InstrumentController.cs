using Microsoft.AspNetCore.Mvc;
using TradingEngine.Api.DTOs.Instrument;
using TradingEngine.Core.Entities;
using TradingEngine.Infrastructure.Persistence.Repositories;

namespace TradingEngine.Api.Controllers
{
    [ApiController]
    public class InstrumentController : ControllerBase
    {
        private readonly IInstrumentRepository instrumentRepository;
        public InstrumentController(IInstrumentRepository instrumentRepository)
        {
            this.instrumentRepository = instrumentRepository;
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
    }
}
