using Microsoft.AspNetCore.Mvc;
using System.Net.NetworkInformation;
using TradingEngine.Api.DTOs.Position;
using TradingEngine.Core.Interfaces.Repositories;
using TradingEngine.Core.Entities;

namespace TradingEngine.Api.Controllers
{
    [ApiController]
    [Route("api/accounts/{accountId:int}/positions")]
    public class PositionsController : ControllerBase
    {
        private readonly IPositionRepository positionRepository;
        public PositionsController(IPositionRepository positionRepository)
        {
            this.positionRepository = positionRepository;
        }
        [HttpGet]
        public async Task<ActionResult<List<PositionResponseDto>>> GetAllPositionsById(int accountId, CancellationToken ct)
        {
            List<Position>positionList=await positionRepository.GetByAccountIdAsync(accountId, ct);
            List<PositionResponseDto> postionResponseList = new List<PositionResponseDto>();
            foreach(var it in positionList)
            {
                PositionResponseDto position = new PositionResponseDto
                {
                    AccountId=it.AccountId,
                    InstrumentId=it.InstrumentId,
                    Quantity=it.Quantity,
                    ReservedQuantity=it.ReservedQuantity,
                    AvailableQuantity=it.AvailableQuantity,
                    AverageCost=it.AverageCost
                };
                postionResponseList.Add(position);
            }
            return Ok(postionResponseList);
        }
        [HttpGet("{instrumentId:int}")]
        public async Task<ActionResult<PositionResponseDto>> GetPosition(int accountId, int instrumentId, CancellationToken ct)
        {
            Position? position=await positionRepository.GetByAccountAndInstrumentAsync(accountId, instrumentId, ct);
            if (position == null) return NotFound($"Position with ID {instrumentId} not found.");
            PositionResponseDto response = new PositionResponseDto
            {
                AccountId = position.AccountId,
                InstrumentId = position.InstrumentId,
                Quantity = position.Quantity,
                ReservedQuantity = position.ReservedQuantity,
                AvailableQuantity = position.AvailableQuantity,
                AverageCost = position.AverageCost
            };
            return Ok(response);
        }
    }
}
