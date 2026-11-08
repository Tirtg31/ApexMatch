using Microsoft.AspNetCore.Mvc;
using TradingEngine.Api.DTOs.Order;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Interfaces.Repositories;
using TradingEngine.Core.Services;
using TradingEngine.Infrastructure.Persistence.Repositories;

namespace TradingEngine.Api.Controllers
{
    [ApiController]
    public class OrdersController : ControllerBase
    {
        private readonly IOrderRepository orderRepository;
        private readonly TradingCoordinator tradingCoordinator;
        public OrdersController(IOrderRepository orderRepository, TradingCoordinator tradingCoordinator)
        {
            this.orderRepository = orderRepository;
            this.tradingCoordinator = tradingCoordinator;
        }

        private static OrderResponseDto MapToResponse(Order order)
        {
            return new OrderResponseDto
            {
                OrderId = order.OrderId,
                AccountId = order.AccountId,
                InstrumentId = order.InstrumentId,
                Side = order.Side,
                OrderType = order.OrderType,
                Quantity = order.Quantity,
                LimitPrice = order.LimitPrice,
                FilledQuantity = order.FilledQuantity,
                RemainingQuantity = order.Quantity - order.FilledQuantity,
                Status = order.Status,
                CreatedAt = order.CreatedAt
            };
        }
        [HttpPost("api/orders")]
        public async Task<ActionResult<OrderResponseDto>> PlaceOrder(PlaceOrderRequestDto request, CancellationToken ct)
        {
            Order order = new Order
            {
                AccountId= request.AccountId,
                InstrumentId= request.InstrumentId,
                Side = request.Side,
                OrderType = OrderType.Limit,
                Quantity = request.Quantity,
                LimitPrice= request.LimitPrice
            };
            Order placedOrder=new Order();
            if (request.Side == OrderSide.Buy)
                placedOrder = await tradingCoordinator.PlaceBuyOrderAsync(order, ct);
            else if (request.Side == OrderSide.Sell)
                placedOrder = await tradingCoordinator.PlaceSellOrderAsync(order, ct);

            OrderResponseDto response = MapToResponse(placedOrder);
            return Ok(response);
        }
        [HttpGet("api/orders/{id:int}")]
        public async Task<ActionResult<OrderResponseDto>> GetOrderById(int id, CancellationToken ct)
        {
            Order? order =await orderRepository.GetByIdAsync(id, ct);
            if(order==null) return NotFound($"Order with ID {id} not found.");
            OrderResponseDto response = MapToResponse(order);
            return Ok(response);
        }
        [HttpGet("api/account/{id:int}/orders")]
        public async Task<ActionResult<List<OrderResponseDto>>> GetOrderByAccount(int id, CancellationToken ct)
        {
            List<Order> orderList = await orderRepository.GetByAccountIdAsync(id, ct);
            List<OrderResponseDto> responseList = new List<OrderResponseDto>();
            foreach(var it in orderList)
            {
                OrderResponseDto res = MapToResponse(it);
                responseList.Add(res);
            }
            return Ok(responseList);
        }
        [HttpGet("api/instruments/{id:int}/orders/open")]
        public async Task<ActionResult<List<OrderResponseDto>>> GetOpenOrdersByInstrument(int id, CancellationToken ct)
        {
            List<Order> orderList = await orderRepository.GetOpenOrdersByInstrumentAsync(id, ct);
            List<OrderResponseDto> responseList = new List<OrderResponseDto>();
            foreach (var it in orderList)
            {
                OrderResponseDto res = MapToResponse(it);
                responseList.Add(res);
            }
            return Ok(responseList);
        }
        [HttpPost("api/orders/{id:int}/cancel")]
        public async Task<IActionResult> CancelOrder(int id, CancellationToken ct)
        {
            Order? request = await orderRepository.GetByIdAsync(id, ct);
            if (request == null) return NotFound($"Order with ID {id} not found.");

            if (request.Side == OrderSide.Buy)
                await tradingCoordinator.CancelBuyOrderAsync(id, ct);
            else if (request.Side == OrderSide.Sell)
                await tradingCoordinator.CancelSellOrderAsync(id, ct);
            return Ok();
        }
    }
}
