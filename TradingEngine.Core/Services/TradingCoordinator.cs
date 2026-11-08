using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Interfaces.Repositories;

namespace TradingEngine.Core.Services
{
    public class TradingCoordinator
    {
        private readonly SettlementService settlementService;
        private readonly OrderMatchingEngine orderMatchingEngine;
        private readonly IOrderRepository orderRepository;
        public readonly OrderService orderService;
        private readonly AccountService accountService;

        private readonly object tradingLock = new();
        private readonly SemaphoreSlim tradingGate = new(1, 1);
        private readonly TradingRuntimeState runtimeState;
        private bool requiresRecovery;
        public TradingCoordinator(SettlementService settlementService, OrderMatchingEngine orderMatchingEngine, OrderService orderService)
        {
            this.settlementService = settlementService;
            this.orderMatchingEngine = orderMatchingEngine;
            this.orderService = orderService;
        }
        public TradingCoordinator(SettlementService settlementService, OrderMatchingEngine orderMatchingEngine, OrderService orderService, IOrderRepository orderRepository)
        {
            this.settlementService = settlementService;
            this.orderMatchingEngine = orderMatchingEngine;
            this.orderService = orderService;
            this.orderRepository = orderRepository;
        }
        public TradingCoordinator(SettlementService settlementService, OrderMatchingEngine orderMatchingEngine, OrderService orderService, IOrderRepository orderRepository, TradingRuntimeState runtimeState)

        {
            this.settlementService = settlementService;
            this.orderMatchingEngine = orderMatchingEngine;
            this.orderService = orderService;
            this.orderRepository = orderRepository;
            this.runtimeState = runtimeState;
        }
        public TradingCoordinator(SettlementService settlementService, OrderMatchingEngine orderMatchingEngine, OrderService orderService, IOrderRepository orderRepository, AccountService accountService, TradingRuntimeState runtimeState)

        {
            this.settlementService = settlementService;
            this.orderMatchingEngine = orderMatchingEngine;
            this.orderService = orderService;
            this.orderRepository = orderRepository;
            this.accountService = accountService;
            this.runtimeState = runtimeState;
        }
        private void EnsureReady()
        {
            if(runtimeState.RequiresRecovery)
                throw new InvalidOperationException("Trading is suspended until book recovery completes.");
        }
        public Order PlaceBuyOrder(Order order)
        {
            lock(tradingLock)
            {
                if (order == null) throw new ArgumentNullException("Order is invalid");
                return orderService.PlaceBuyOrder(order);
            }
        }
        public Order PlaceSellOrder(Order order)
        {
            lock (tradingLock)
            {
                if (order == null) throw new ArgumentNullException("Order is invalid");
                return orderService.PlaceSellOrder(order);
            }
        }
        public bool CancelBuyOrder(int orderId)
        {
            lock(tradingLock)
            {
                if (orderId <= 0) throw new ArgumentNullException("Order id is invalid");
                return orderService.CancelBuyOrder(orderId);
            }
        }
        public bool CancelSellOrder(int orderId)
        {
            lock (tradingLock)
            {
                if (orderId <= 0) throw new ArgumentNullException("Order id is invalid");
                return orderService.CancelSellOrder(orderId);
            }
        }
        public List<Execution>MatchAndSettle(int instrumentId)
        {
            lock(tradingLock)
            {
                if(instrumentId<=0) throw new ArgumentOutOfRangeException("Instrument id is invalid");
                List<Execution> executionList=orderMatchingEngine.LimitOrderMatcher(instrumentId);
                foreach(Execution execution in executionList)
                {
                    Order? buyOrder = orderMatchingEngine.GetOrderById(execution.BuyOrderId);
                    Order? sellOrder = orderMatchingEngine.GetOrderById(execution.SellOrderId);
                    if (buyOrder == null || sellOrder == null) throw new ArgumentException("Buy/Sell order is null");
                    settlementService.SettleExecution(execution, buyOrder, sellOrder);
                }
                return executionList;
            }
        }

        public async Task<List<Execution>> MatchAndSettleAsync(int instrumentId, CancellationToken ct = default)
        {
            if (instrumentId <= 0)
                throw new ArgumentOutOfRangeException(nameof(instrumentId));

            await runtimeState.TradingGate.WaitAsync(ct);

            try
            {
                EnsureReady();

                List<Execution> executions = new();

                while (true)
                {
                    // 1. Find next executable pair.
                    ProposedMatch? match =
                        orderMatchingEngine.ProposeMatch(instrumentId);

                    if (match == null)
                        break;

                    Execution execution;

                    // 2. Commit settlement in Oracle.
                    try
                    {
                        execution =
                            await settlementService.SettleAsync(match, ct);
                    }
                    catch
                    {
                        // The DB transaction may have rolled back,
                        // or its commit outcome may be uncertain.
                        // Reconcile with Oracle before trading again.
                        runtimeState.RequiresRecovery = true;
                        throw;
                    }

                    // 3. Oracle commit succeeded.
                    // Update the in-memory order book.
                    try
                    {
                        orderMatchingEngine.ConfirmMatch(match);
                    }
                    catch (Exception ex)
                    {
                        // DB is already committed.
                        // Never try to undo it here.
                        runtimeState.RequiresRecovery = true;

                        throw new InvalidOperationException(
                            "Settlement committed, but matching engine " +
                            "synchronization failed. Trading suspended " +
                            "until order-book recovery.", ex);
                    }

                    // 4. Execution persisted and book synchronized.
                    executions.Add(execution);
                }

                return executions;
            }
            finally
            {
                runtimeState.TradingGate.Release();
            }
        }
        public async Task<Order>PlaceBuyOrderAsync(Order order, CancellationToken ct=default)
        {
            await runtimeState.TradingGate.WaitAsync(ct);
            try
            {
                EnsureReady();
                Order persistedOrder= await orderService.PlaceBuyOrderAsync(order,ct);
                try
                {
                    orderMatchingEngine.RegisterPersistedOrder(persistedOrder);
                }
                catch (Exception ex)
                {
                    runtimeState.RequiresRecovery = true;
                    throw new InvalidOperationException("Order persisted, but book registration failed. " + "Engine requires reconciliation.", ex);
                }
                return persistedOrder;
            }
            finally
            {
                runtimeState.TradingGate.Release();
            }
        }
        public async Task<Order> PlaceSellOrderAsync(Order order, CancellationToken ct=default)
        {
            await runtimeState.TradingGate.WaitAsync(ct);
            try
            {
                EnsureReady();
                Order persistedOrder = await orderService.PlaceSellOrderAsync(order, ct);
                try
                {
                    orderMatchingEngine.RegisterPersistedOrder(persistedOrder);
                }
                catch (Exception ex)
                {
                    runtimeState.RequiresRecovery = true;
                    throw new InvalidOperationException("Order persisted, but book registration failed. " + "Engine requires reconciliation.", ex);
                }
                return persistedOrder;
            }
            finally
            {
                runtimeState.TradingGate.Release();
            }
        }
        public async Task CancelBuyOrderAsync(int orderId, CancellationToken ct=default)
        {
            await runtimeState.TradingGate.WaitAsync(ct);
            try
            {
                EnsureReady();
                await orderService.CancelBuyOrderAsync(orderId, ct);
                try
                {
                    bool cancelled = orderMatchingEngine.CancelOrder(orderId);
                    if (!cancelled)
                        throw new InvalidOperationException("Order not found");
                }
                catch(Exception ex)
                {
                    runtimeState.RequiresRecovery = true;
                    throw new InvalidOperationException("Database cancellation succeeded, but " + "matching engine synchronization failed. " + "Trading is suspended pending recovery.", ex);
                }
            }
            finally
            { runtimeState.TradingGate.Release(); }
        }
        public async Task CancelSellOrderAsync(int orderId, CancellationToken ct = default)
        {
            await runtimeState.TradingGate.WaitAsync(ct);
            try
            {
                EnsureReady();
                await orderService.CancelSellOrderAsync(orderId, ct);
                try
                {
                    bool cancelled = orderMatchingEngine.CancelOrder(orderId);
                    if (!cancelled)
                        throw new InvalidOperationException("Order not found");
                }
                catch (Exception ex)
                {
                    runtimeState.RequiresRecovery = true;
                    throw new InvalidOperationException("Database cancellation succeeded, but " + "matching engine synchronization failed. " + "Trading is suspended pending recovery.", ex);
                }
            }
            finally
            { runtimeState.TradingGate.Release(); }
        }
        public async Task RecoverOrderBookAsync(CancellationToken ct = default)
        {
            await runtimeState.TradingGate.WaitAsync(ct);
            try
            {
                runtimeState.RequiresRecovery = true;
                List<Order> openOrders = await orderRepository.GetAllOpenOrdersAsync(ct);
                orderMatchingEngine.RebuildFromPersistedOrders(openOrders);
                runtimeState.RequiresRecovery = false;
            }
            finally
            {
                runtimeState.TradingGate.Release();
            }
        }

        public async Task DepositAmountAsync(int accountId, decimal amount, CancellationToken ct = default)
        {
            await runtimeState.TradingGate.WaitAsync(ct);
            try
            {
                EnsureReady();
                await accountService.DepositFundsAsync(accountId, amount, ct);
            }
            finally
            {
                runtimeState.TradingGate.Release();
            }
        }
        public async Task WithdrawAmountAsync(int accountId, decimal amount, CancellationToken ct = default)
        {
            await runtimeState.TradingGate.WaitAsync(ct);
            try
            {
                EnsureReady();
                await accountService.WithdrawFundsAsync(accountId, amount, ct);
            }
            finally
            {
                runtimeState.TradingGate.Release();
            }
        }
    }
}
