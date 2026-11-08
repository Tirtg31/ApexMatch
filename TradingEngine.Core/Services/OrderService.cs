using System.Collections.Concurrent;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Interfaces.Repositories;
using TradingEngine.Core.Services;

public class OrderService //Add Concurrency token to Order Enitity
{
    private readonly AccountService accountService;
    private readonly PositionService positionService;
    private readonly OrderMatchingEngine matchingEngine;

    private readonly IUnitOfWork unitOfWork;
    public readonly IAccountRepository accountRepository;
    public readonly IOrderRepository orderRepository;
    public OrderService(AccountService accountService, OrderMatchingEngine matchingEngine, PositionService positionService)
    {
        this.accountService = accountService;
        this.matchingEngine = matchingEngine;
        this.positionService = positionService;
    }
    public OrderService(AccountService accountService, OrderMatchingEngine matchingEngine, PositionService positionService, IAccountRepository accountRepository, IUnitOfWork unitOfWork, IOrderRepository orderRepository)
    {
        this.accountService = accountService;
        this.matchingEngine = matchingEngine;
        this.positionService = positionService;
        this.accountRepository = accountRepository;
        this.unitOfWork = unitOfWork;
        this.orderRepository = orderRepository;
    }

    public Order PlaceBuyOrder(Order order)
    {
        ValidateOrder(order, OrderSide.Buy);
        decimal requiredAmount = order.Quantity * order.LimitPrice.Value;
        accountService.ReserveFunds(order.AccountId, requiredAmount);
        try
        {
            Order acceptedOrder = matchingEngine.AddOrder(order);
            if (acceptedOrder == null)
                throw new InvalidOperationException("Matching engine rejected the order");
            return acceptedOrder;
        }
        catch
        {
            accountService.ReleaseFunds(order.AccountId, requiredAmount);
            throw;
        }
    }
    public async Task<Order> PlaceBuyOrderAsync(Order order, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.Side != OrderSide.Buy)
            throw new ArgumentException("Incorrect order side");
        if (order.OrderType != OrderType.Limit)
            throw new ArgumentException("Only limit orders are supported");
        if (order.AccountId <= 0 || order.InstrumentId <= 0)
            throw new ArgumentException("Invalid account or instrument ID");
        if (order.Quantity <= 0 || order.LimitPrice == null || order.LimitPrice <= 0)
            throw new ArgumentException("Invalid quantity or price");

        decimal requiredAmount = checked(order.Quantity * order.LimitPrice.Value);

        // Don't trust caller-controlled lifecycle fields.
        order.OrderId = 0;
        order.FilledQuantity = 0;
        order.Status = OrderStatus.Pending;
        order.CreatedAt = DateTime.UtcNow;

        await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            await accountService.ReserveFundsWithoutCommitAsync(order.AccountId, requiredAmount, token);
            await orderRepository.AddAsync(order, token);
            Account? account = await accountRepository.GetByIdAsync(order.AccountId, token);
            if (account == null) throw new ArgumentException("Account not found.");
            if (!account.IsActive) throw new InvalidOperationException("Cannot place orders from an inactive account.");
            //Adding to MatchingEngine is still pending
            return true;
        }, ct);

        return order;
    }
    public Order PlaceSellOrder(Order order)
    {
        ValidateOrder(order,OrderSide.Sell);
        positionService.ReserveQuantity(order.AccountId, order.InstrumentId, order.Quantity);
        try
        {
            Order acceptedOrder = matchingEngine.AddOrder(order);
            if (acceptedOrder == null)
                throw new InvalidOperationException("Matching engine rejected the order");
            return acceptedOrder;
        }
        catch
        {
            positionService.ReleaseReservedQuantity(order.AccountId, order.InstrumentId, order.Quantity);
            throw;
        }
    }
    public async Task<Order> PlaceSellOrderAsync(Order order, CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.Side != OrderSide.Sell)
            throw new ArgumentException("Incorrect order side");
        if (order.OrderType != OrderType.Limit)
            throw new ArgumentException("Only limit orders are supported");
        if (order.AccountId <= 0 || order.InstrumentId <= 0)
            throw new ArgumentException("Invalid account or instrument ID");
        if (order.Quantity <= 0 || order.LimitPrice == null || order.LimitPrice <= 0)
            throw new ArgumentException("Invalid quantity or price");

        // Don't trust caller-controlled lifecycle fields.
        order.OrderId = 0;
        order.FilledQuantity = 0;
        order.Status = OrderStatus.Pending;
        order.CreatedAt = DateTime.UtcNow;

        await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            await positionService.ReserveQuantityWithoutCommitAsync(order.AccountId, order.InstrumentId, order.Quantity, token);
            await orderRepository.AddAsync(order, token);
            Account? account = await accountRepository.GetByIdAsync(order.AccountId, token);
            if (account == null) throw new ArgumentException("Account not found.");
            if (!account.IsActive) throw new InvalidOperationException("Cannot place orders from an inactive account.");
            //Adding to MatchingEngine is still pending
            return true;
        }, ct);

        return order;
    }
    public bool CancelBuyOrder(int orderId)
    {
        Order? order = matchingEngine.GetOrderById(orderId);
        if (order == null)  throw new ArgumentException("Order not found.");
        if (order.Side != OrderSide.Buy) throw new InvalidOperationException("Not a buy order.");

        bool isCancellationSuccessful = matchingEngine.CancelOrder(orderId);
        if (!isCancellationSuccessful)
            return false;

        int remainingQuantity = order.Quantity - order.FilledQuantity;
        decimal amountToRelease = remainingQuantity * order.LimitPrice!.Value;
        accountService.ReleaseFunds(order.AccountId, amountToRelease);
        return true;
    }
    public async Task CancelBuyOrderAsync(int orderId, CancellationToken ct = default)
    {
        if (orderId <= 0)
            throw new ArgumentOutOfRangeException(nameof(orderId));

        await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
                Order? order = await orderRepository.GetByIdAsync(orderId, token);
                if (order == null)
                    throw new ArgumentException("Order not found.");
                if (order.Side != OrderSide.Buy)
                    throw new InvalidOperationException("Not a buy order.");
                if (order.Status != OrderStatus.Pending && order.Status != OrderStatus.PartiallyFilled)
                    throw new InvalidOperationException("Only pending or partially filled orders can be cancelled.");
                int remainingQuantity = order.Quantity - order.FilledQuantity;
                if (remainingQuantity <= 0)
                    throw new InvalidOperationException("No remaining quantity to cancel.");

                decimal amountToRelease = remainingQuantity * order.LimitPrice!.Value;
                await accountService.ReleaseFundsWithoutCommitAsync(order.AccountId, amountToRelease, token);
                order.Status = OrderStatus.Cancelled;

                // Database changes are committed by UnitOfWork.
                // Matching-engine synchronization is pending.
                return true;
         }, ct);
    }

    public bool CancelSellOrder(int orderId)
    {
        Order? order = matchingEngine.GetOrderById(orderId);

        if (order == null) throw new ArgumentException("Order not found.");
        if (order.Side != OrderSide.Sell) throw new InvalidOperationException("Not a sell order.");

        bool isCancellationSuccessful = matchingEngine.CancelOrder(orderId);
        if (!isCancellationSuccessful)
            return false;

        int remainingQuantity = order.Quantity - order.FilledQuantity;
        positionService.ReleaseReservedQuantity(order.AccountId, order.InstrumentId, remainingQuantity);
        return true;
    }
    public async Task CancelSellOrderAsync(int orderId, CancellationToken ct = default)
    {
        if (orderId <= 0)
            throw new ArgumentOutOfRangeException(nameof(orderId));

        await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
                Order? order = await orderRepository.GetByIdAsync(orderId, token);

                if (order == null)
                    throw new ArgumentException("Order not found.");
                if (order.Side != OrderSide.Sell)
                    throw new InvalidOperationException("Not a sell order.");
                if (order.Status != OrderStatus.Pending && order.Status != OrderStatus.PartiallyFilled)
                    throw new InvalidOperationException("Only pending or partially filled orders can be cancelled.");
                int remainingQuantity = order.Quantity - order.FilledQuantity;
                    if (remainingQuantity <= 0)
                        throw new InvalidOperationException("No remaining quantity to cancel.");

                await positionService.ReleaseReservedQuantityWithoutCommitAsync(order.AccountId, order.InstrumentId, remainingQuantity, token);
                order.Status = OrderStatus.Cancelled;

                // Matching-engine synchronization pending.
                return true;
        }, ct);
    }

    private void ValidateOrder(Order order, OrderSide expectedSide)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (order.Side != expectedSide)
            throw new ArgumentException("Incorrect order side");

        if (order.OrderType != OrderType.Limit)
            throw new ArgumentException("Only limit orders are supported");

        if (order.AccountId <= 0 || order.InstrumentId <= 0)
            throw new ArgumentException("Invalid account or instrument ID");

        if (order.Quantity <= 0 || order.LimitPrice == null || order.LimitPrice <= 0)
            throw new ArgumentException("Invalid quantity or price");

        Account? account = accountService.GetAccountById(order.AccountId);

        if (account == null)
            throw new ArgumentException("Account not found");

        if (!account.IsActive)
            throw new InvalidOperationException("Account is inactive");
    }
}