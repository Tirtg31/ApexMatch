using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Interfaces.Repositories;

namespace TradingEngine.Core.Services
{
    public class SettlementService
    {
        private readonly AccountService accountService;
        private readonly PositionService positionService;
        private readonly IUnitOfWork unitOfWork;
        private readonly IOrderRepository orderRepository;
        private readonly IExecutionRepository executionRepository;
        private readonly object settlementLock = new();

        public SettlementService(AccountService accountService, PositionService positionService, IUnitOfWork unitOfWork, IOrderRepository orderRepository, IExecutionRepository executionRepository)
        {
            this.accountService = accountService;
            this.positionService = positionService;
            this.unitOfWork = unitOfWork;
            this.orderRepository = orderRepository;
            this.executionRepository = executionRepository;
        }
        public SettlementService(AccountService accountService, PositionService positionService)
        {
            this.accountService = accountService;
            this.positionService = positionService;
        }
        public void SettleExecution(Execution execution, Order buyOrder, Order sellOrder)
        {
            lock (settlementLock)
            {
                ArgumentNullException.ThrowIfNull(execution);
                ArgumentNullException.ThrowIfNull(buyOrder);
                ArgumentNullException.ThrowIfNull(sellOrder);
                if (execution.BuyOrderId != buyOrder.OrderId || execution.SellOrderId != sellOrder.OrderId) throw new ArgumentException("Not part of the same execution.");
                if (buyOrder.Side != OrderSide.Buy || sellOrder.Side != OrderSide.Sell) throw new ArgumentException("Invalid order sides.");
                if (buyOrder.InstrumentId != sellOrder.InstrumentId) throw new ArgumentException("Instrument mismatch.");
                if (execution.ExecutedPrice <= 0) throw new ArgumentException("Executed price must be greater than 0");
                if (execution.ExecutedQuantity <= 0) throw new ArgumentException("Executed quantity must be greater than 0");
                if (buyOrder.LimitPrice is null || buyOrder.LimitPrice <= 0) throw new ArgumentException("Invalid buy limit price.");
                if (sellOrder.LimitPrice is null || sellOrder.LimitPrice <= 0) throw new ArgumentException("Invalid sell limit price.");
                if (execution.ExecutedQuantity > buyOrder.Quantity || execution.ExecutedQuantity > sellOrder.Quantity) throw new ArgumentException("Executed quantity exceeds order quantity.");
                if (execution.ExecutedPrice > buyOrder.LimitPrice.Value || execution.ExecutedPrice < sellOrder.LimitPrice.Value) throw new ArgumentException("Execution price violates order limits.");

                Account? buyer = accountService.GetAccountById(buyOrder.AccountId);
                Account? seller = accountService.GetAccountById(sellOrder.AccountId);
                if (buyer == null || seller == null) throw new ArgumentException("Buyer/Seller Account is missing");
                if (!buyer.IsActive || !seller.IsActive) throw new ArgumentException("Buyer/Seller is inactive");

                decimal executionAmount = execution.ExecutedQuantity * execution.ExecutedPrice;
                decimal reservedAmount = execution.ExecutedQuantity * buyOrder.LimitPrice!.Value;

                // Buyer pays and gets unused reserved money released
                accountService.SettleBuyer(execution.ExecutionId, buyer.AccountId, reservedAmount, executionAmount);
                // Seller receives proceeds
                accountService.SettleSeller(execution.ExecutionId, seller.AccountId, executionAmount);
                // Buyer receives shares
                positionService.AddPosition(buyer.AccountId, buyOrder.InstrumentId, execution.ExecutedQuantity, execution.ExecutedPrice);
                // Seller delivers reserved shares
                positionService.SettleSellPosition(seller.AccountId, sellOrder.InstrumentId, execution.ExecutedQuantity);
            }
        }

        public async Task<Execution> SettleAsync(ProposedMatch match, CancellationToken ct = default)
        {
            ValidateProposedMatch(match);

            if (unitOfWork == null ||
                orderRepository == null ||
                executionRepository == null)
                throw new InvalidOperationException(
                    "Database settlement dependencies not configured.");

            return await unitOfWork.ExecuteInTransactionAsync(
                async token =>
                {
                    // 1. Load persisted orders.
                    Order buyOrder = await orderRepository.GetByIdAsync(
                        match.BuyOrderId, token)
                        ?? throw new InvalidOperationException(
                            "Buy order not found.");

                    Order sellOrder = await orderRepository.GetByIdAsync(
                        match.SellOrderId, token)
                        ?? throw new InvalidOperationException(
                            "Sell order not found.");

                    // 2. Validate both orders.
                    ValidateOrders(match, buyOrder, sellOrder);

                    // 3. Calculate settlement amounts.
                    decimal reservedAmount = checked(
                        match.Quantity * buyOrder.LimitPrice!.Value);

                    decimal executionAmount = checked(
                        match.Quantity * match.Price);

                    // 4. Settle cash.
                    await accountService.SettleBuyerWithoutCommitAsync(
                        buyOrder.AccountId,
                        reservedAmount,
                        executionAmount,
                        token);

                    await accountService.SettleSellerWithoutCommitAsync(
                        sellOrder.AccountId,
                        executionAmount,
                        token);

                    // 5. Settle positions.
                    await positionService.SettleSellPositionWithoutCommitAsync(
                        sellOrder.AccountId,
                        match.InstrumentId,
                        match.Quantity,
                        token);

                    await positionService.IncreasePositionWithoutCommitAsync(
                        buyOrder.AccountId,
                        match.InstrumentId,
                        match.Quantity,
                        match.Price,
                        token);

                    // 6. Update persisted orders.
                    ApplyOrderFill(buyOrder, match.Quantity);
                    ApplyOrderFill(sellOrder, match.Quantity);

                    // 7. Create execution.
                    Execution execution = new Execution
                    {
                        BuyOrderId = buyOrder.OrderId,
                        SellOrderId = sellOrder.OrderId,
                        ExecutedQuantity = match.Quantity,
                        ExecutedPrice = match.Price,
                        ExecutedAt = DateTime.UtcNow
                    };

                    await executionRepository.AddAsync(execution, token);

                    // EfUnitOfWork handles SaveChanges and COMMIT.
                    return execution;

                }, ct);
        }
        private static void ValidateProposedMatch(ProposedMatch match)
        {
            ArgumentNullException.ThrowIfNull(match);

            if (match.BuyOrderId <= 0 ||
                match.SellOrderId <= 0 ||
                match.BuyOrderId == match.SellOrderId)
                throw new ArgumentException("Invalid order IDs.");

            if (match.InstrumentId <= 0)
                throw new ArgumentException("Invalid instrument ID.");

            if (match.Quantity <= 0)
                throw new ArgumentException(
                    "Execution quantity must be positive.");

            if (match.Price <= 0)
                throw new ArgumentException(
                    "Execution price must be positive.");
        }
        private static void ValidateOrders(ProposedMatch match, Order buyOrder, Order sellOrder)
        {
            if (buyOrder.Side != OrderSide.Buy ||
                sellOrder.Side != OrderSide.Sell)
                throw new InvalidOperationException(
                    "Invalid order sides.");

            if (buyOrder.InstrumentId != match.InstrumentId ||
                sellOrder.InstrumentId != match.InstrumentId)
                throw new InvalidOperationException(
                    "Instrument mismatch.");

            if (buyOrder.AccountId == sellOrder.AccountId)
                throw new InvalidOperationException(
                    "Self-trading is not supported.");

            if (buyOrder.Status is not
                    (OrderStatus.Pending or OrderStatus.PartiallyFilled) ||
                sellOrder.Status is not
                    (OrderStatus.Pending or OrderStatus.PartiallyFilled))
                throw new InvalidOperationException(
                    "Both orders must be open.");

            if (buyOrder.LimitPrice is not > 0 ||
                sellOrder.LimitPrice is not > 0)
                throw new InvalidOperationException(
                    "Invalid limit prices.");

            if (buyOrder.LimitPrice < sellOrder.LimitPrice)
                throw new InvalidOperationException(
                    "Orders do not cross.");

            int buyRemaining =
                buyOrder.Quantity - buyOrder.FilledQuantity;

            int sellRemaining =
                sellOrder.Quantity - sellOrder.FilledQuantity;

            if (buyRemaining <= 0 || sellRemaining <= 0 ||
                match.Quantity != Math.Min(buyRemaining, sellRemaining))
                throw new InvalidOperationException(
                    "Proposed match quantity is stale.");

            decimal expectedPrice =
                buyOrder.CreatedAt < sellOrder.CreatedAt
                    ? buyOrder.LimitPrice.Value
                    : sellOrder.CreatedAt < buyOrder.CreatedAt
                        ? sellOrder.LimitPrice.Value
                        : buyOrder.OrderId < sellOrder.OrderId
                            ? buyOrder.LimitPrice.Value
                            : sellOrder.LimitPrice.Value;

            if (match.Price != expectedPrice)
                throw new InvalidOperationException(
                    "Proposed execution price is stale.");
        }
        private static void ApplyOrderFill(Order order, int quantity)
        {
            if (quantity <= 0)
                throw new ArgumentOutOfRangeException(nameof(quantity));

            int newFilledQuantity = checked(
                order.FilledQuantity + quantity);

            if (newFilledQuantity > order.Quantity)
                throw new InvalidOperationException(
                    "Order cannot be overfilled.");

            order.FilledQuantity = newFilledQuantity;

            order.Status =
                order.FilledQuantity == order.Quantity
                    ? OrderStatus.Filled
                    : OrderStatus.PartiallyFilled;
        }



    }
}



/*
SettlementService
│
├── SettleExecution()                  // Existing in-memory method
│
├── SettleAsync()                      // EF Core orchestration
│
├── ValidateProposedMatch()            // Proposal validation
│
├── ValidateOrders()                   // Persisted-order validation
│
└── ApplyOrderFill()                   // Order-state update
 */