using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Identity.Client;
using TradingEngine.Api.DTOs.Execution;
using TradingEngine.Api.DTOs.Trading;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Interfaces.Repositories;
using TradingEngine.Core.Services;

namespace TradingEngine.Api.Controllers
{
    [ApiController]
    public class MatchController : ControllerBase
    {
        private readonly TradingCoordinator tradingCoordinator;
        private readonly IExecutionRepository executionRepository;
        public MatchController(TradingCoordinator tradingCoordinator, IExecutionRepository executionRepository)
        {
            this.tradingCoordinator = tradingCoordinator;
            this.executionRepository = executionRepository;
        }
        private static ExecutionResponseDto MapToResponse(Execution execution)
        {
            return new ExecutionResponseDto
            {
                ExecutionId = execution.ExecutionId,
                BuyOrderId = execution.BuyOrderId,
                SellOrderId = execution.SellOrderId,
                ExecutedQuantity = execution.ExecutedQuantity,
                ExecutedPrice = execution.ExecutedPrice,
                ExecutedAt = execution.ExecutedAt
            };
        }

        [HttpPost("api/trading/match/{instrumentId}")]
        public async Task<ActionResult<MatchResultDto>> Match(int instrumentId, CancellationToken ct)
        {
            if (instrumentId <= 0)
                return BadRequest("Invalid instrument ID.");
            List<Execution> executionList = await tradingCoordinator.MatchAndSettleAsync(instrumentId, ct);
            List<MatchResultDto> responseList=new List<MatchResultDto>();
            MatchResultDto response = new MatchResultDto
            {
                InstrumentId = instrumentId,
                TotalExecutions = executionList.Count,
                TotalExecutedQuantity = executionList.Sum(e => e.ExecutedQuantity),
                TotalTradedValue = executionList.Sum(e => e.ExecutedQuantity * e.ExecutedPrice),
                Executions = executionList.Select(MapToResponse).ToList()
            };
            return Ok(response);
        }
        [HttpGet("api/executions/{executionId:int}")]
        public async Task<ActionResult<ExecutionResponseDto>>GetExecutionById(int executionId, CancellationToken ct)
        {
            Execution? exec = await executionRepository.GetByIdAsync(executionId, ct);
            if (exec == null) return NotFound($"Execution id {executionId} not found");
            ExecutionResponseDto response = MapToResponse(exec);
            return Ok(response);
        }
        [HttpGet("api/orders/{orderId:int}/executions")]
        public async Task<ActionResult<List<ExecutionResponseDto>>>GetExecutionsByOrder(int orderId, CancellationToken ct)
        {
            List<Execution>executionList=await executionRepository.GetByOrderIdAsync(orderId, ct);
            List<ExecutionResponseDto> responseList = new List<ExecutionResponseDto>();
            foreach(var it in executionList)
            {
                ExecutionResponseDto exec = MapToResponse(it);
                responseList.Add(exec);
            }
            return responseList;
        }
    }
}
