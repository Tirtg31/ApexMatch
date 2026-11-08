
using System;
using System.Collections.Generic;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Interfaces.Repositories;

namespace TradingEngine.Core.Services
{
    public class OrderMatchingEngine
    {
        private Dictionary<int, OrderBook> OrderBooks = new();
        private Dictionary<int, Order> Orders = new();
        private readonly object orderMatchingEngineLock = new();

        private int _nextExecutionId = 1;
        private int _nextOrderId = 1;
        private long _nextSequence = 1;

        private static Order CloneOrder(Order order)
        {
            return new Order
            {
                OrderId = order.OrderId,
                AccountId = order.AccountId,
                InstrumentId= order.InstrumentId,
                Side= order.Side,
                OrderType = order.OrderType,
                Quantity = order.Quantity,
                LimitPrice = order.LimitPrice,
                FilledQuantity = order.FilledQuantity,
                Status = order.Status,
                CreatedAt = order.CreatedAt
            };
        }
        public Order AddOrder(Order order)
        {
            lock (orderMatchingEngineLock)
            {
                if (order == null)
                    throw new ArgumentNullException(nameof(order));

                //Validate
                if (order.InstrumentId <= 0)
                    throw new ArgumentException("Invalid instrument ID.");

                if (order.AccountId <= 0)
                    throw new ArgumentException("Invalid account ID.");

                if (order.Quantity <= 0)
                    throw new ArgumentException("Quantity must be positive.");

                if (order.LimitPrice == null || order.LimitPrice <= 0)
                    throw new ArgumentException("Limit price must be positive.");

                if (!Enum.IsDefined(typeof(OrderSide), order.Side))
                    throw new ArgumentException("Invalid order side.");

                if (!Enum.IsDefined(typeof(OrderType), order.OrderType))
                    throw new ArgumentException("Invalid order type.");

                if (order.FilledQuantity != 0)
                    throw new ArgumentException("New orders cannot already be filled.");

                //Create OrderBook
                if (!OrderBooks.TryGetValue(order.InstrumentId, out OrderBook? orderBook))
                {
                    orderBook = new OrderBook(order.InstrumentId);
                    OrderBooks.Add(order.InstrumentId, orderBook);
                }

                Order internalOrder = CloneOrder(order);

                // Initialize the internal order
                internalOrder.OrderId = _nextOrderId++;
                internalOrder.CreatedAt = DateTime.UtcNow;
                internalOrder.FilledQuantity = 0;
                internalOrder.Status = OrderStatus.Pending;

                long sequence = _nextSequence++;
                
                if (internalOrder.Side == OrderSide.Buy)
                    orderBook.PushBidQueue(internalOrder, sequence);
                else
                    orderBook.PushAskQueue(internalOrder, sequence);

                Orders.Add(internalOrder.OrderId, internalOrder);
                return CloneOrder(internalOrder);
            }

        }

        public Order? GetOrderById(int orderId)
        {
            lock (orderMatchingEngineLock)
            {
                if (!Orders.TryGetValue(orderId, out Order? order))
                    return null;
                return CloneOrder(order);
            }
        }

        public List<Execution> LimitOrderMatcher(int instrumentId)
        {
            lock (orderMatchingEngineLock)
            {
                List<Execution> result = new();

                if (!OrderBooks.TryGetValue(instrumentId, out OrderBook? orderBook))
                    return result;

                while (orderBook.GetBidQueueSize() > 0 && orderBook.GetAskQueueSize() > 0)
                {
                    Order? buyOrder = orderBook.GetHighestBid();
                    Order? sellOrder = orderBook.GetLowestAsk();

                    if (buyOrder == null || sellOrder == null)
                        break;

                    if (buyOrder.LimitPrice!.Value < sellOrder.LimitPrice!.Value)
                        break;

                    decimal executionPrice = buyOrder.OrderId < sellOrder.OrderId ? buyOrder.LimitPrice.Value : sellOrder.LimitPrice.Value;

                    int buyRemaining = buyOrder.Quantity - buyOrder.FilledQuantity;
                    int sellRemaining = sellOrder.Quantity - sellOrder.FilledQuantity;
                    int matchedQuantity = Math.Min(buyRemaining, sellRemaining);

                    if (matchedQuantity <= 0)
                        throw new InvalidOperationException("Order contains no remaining quantity.");

                    buyOrder.FilledQuantity += matchedQuantity;
                    sellOrder.FilledQuantity += matchedQuantity;

                    if (buyOrder.FilledQuantity == buyOrder.Quantity)
                    {
                        buyOrder.Status = OrderStatus.Filled;
                        orderBook.PopBidQueue();
                    }
                    else
                        buyOrder.Status = OrderStatus.PartiallyFilled;

                    if (sellOrder.FilledQuantity == sellOrder.Quantity)
                    {
                        sellOrder.Status = OrderStatus.Filled;
                        orderBook.PopAskQueue();
                    }
                    else
                        sellOrder.Status = OrderStatus.PartiallyFilled;

                    Execution execution = new Execution
                    {
                        ExecutionId = _nextExecutionId++,
                        BuyOrderId = buyOrder.OrderId,
                        SellOrderId = sellOrder.OrderId,
                        ExecutedQuantity = matchedQuantity,
                        ExecutedPrice = executionPrice,
                        ExecutedAt = DateTime.UtcNow
                    };

                    result.Add(execution);
                }
                return result;
            }
        }
        public bool CancelOrder(int orderId)
        {
            lock (orderMatchingEngineLock)
            {
                if (!Orders.TryGetValue(orderId, out Order? order))
                    return false;
                if (order.Status != OrderStatus.Pending && order.Status != OrderStatus.PartiallyFilled)
                    return false;

                order.Status = OrderStatus.Cancelled;
                return true;
            }
        }
        private void ValidateOrder(Order order)
        {
            if (order.OrderId <= 0)
                throw new ArgumentException("Order must have a persisted ID.");
            if (order.AccountId <= 0 || order.InstrumentId <= 0)
                throw new ArgumentException("Invalid account or instrument ID.");
            if (order.OrderType != OrderType.Limit)
                throw new ArgumentException("Only limit orders are supported.");
            if (!Enum.IsDefined(typeof(OrderSide), order.Side))
                throw new ArgumentException("Invalid order side.");
            if (order.Quantity <= 0)
                throw new ArgumentException("Invalid quantity.");
            if (order.LimitPrice is null || order.LimitPrice <= 0)
                throw new ArgumentException("Invalid limit price.");
            if (order.FilledQuantity < 0 || order.FilledQuantity > order.Quantity)
                throw new ArgumentException("Invalid filled quantity.");
            if (order.CreatedAt == default)
                throw new ArgumentException("Order must have a creation timestamp.");
            if (order.Status != OrderStatus.Pending && order.Status != OrderStatus.PartiallyFilled)
                throw new InvalidOperationException("Only open orders can be registered.");
            if (order.Quantity == order.FilledQuantity)
                throw new InvalidOperationException("Order has no remaining quantity.");
            if (order.Status == OrderStatus.Pending && order.FilledQuantity != 0)
                throw new InvalidOperationException("Pending order cannot have filled quantity.");
            if (order.Status == OrderStatus.PartiallyFilled && order.FilledQuantity == 0)
                throw new InvalidOperationException("Partially filled order must have fills.");
        }
        public Order RegisterPersistedOrder(Order order)
        {
            ArgumentNullException.ThrowIfNull(order);
            lock (orderMatchingEngineLock)
            {
                ValidateOrder(order);
                if (Orders.ContainsKey(order.OrderId))
                    throw new InvalidOperationException("Order already registered.");
                if (!OrderBooks.TryGetValue(order.InstrumentId, out OrderBook? book))
                {
                    book = new OrderBook(order.InstrumentId);
                    OrderBooks.Add(order.InstrumentId, book);
                }
                Order internalOrder = CloneOrder(order);
                long sequence = _nextSequence++;
                if (internalOrder.Side == OrderSide.Buy)
                    book.PushBidQueue(internalOrder, sequence);
                else
                    book.PushAskQueue(internalOrder, sequence);
                Orders.Add(internalOrder.OrderId, internalOrder);
                return CloneOrder(internalOrder);
            }
        }
        public void RebuildFromPersistedOrders(IEnumerable<Order> persistedOrders)
        {
            ArgumentNullException.ThrowIfNull(persistedOrders);
            Dictionary<int, OrderBook> newOrderBooks = new();
            Dictionary<int, Order> newOrders = new();

            long nextSequence = 1;

            foreach (Order order in persistedOrders.OrderBy(o => o.CreatedAt).ThenBy(o => o.OrderId))
            {
                ValidateOrder(order);

                // Check duplicates in temporary state.
                if (newOrders.ContainsKey(order.OrderId))
                    throw new InvalidOperationException("Duplicate persisted order ID.");

                if (!newOrderBooks.TryGetValue(order.InstrumentId, out OrderBook? book))
                {
                    book = new OrderBook(order.InstrumentId);
                    newOrderBooks.Add(order.InstrumentId, book);
                }

                Order internalOrder = CloneOrder(order);
                long sequence = nextSequence++;

                if (internalOrder.Side == OrderSide.Buy)
                    book.PushBidQueue(internalOrder, sequence);
                else
                    book.PushAskQueue(internalOrder, sequence);
                newOrders.Add(internalOrder.OrderId, internalOrder);
            }

            // Publish complete state under the engine lock.
            lock (orderMatchingEngineLock)
            {
                OrderBooks = newOrderBooks;
                Orders = newOrders;
                _nextSequence = nextSequence;
            }
        }
        public ProposedMatch? ProposeMatch(int instrumentId)
        {
            lock(orderMatchingEngineLock)
            {
                if (!OrderBooks.TryGetValue(instrumentId, out OrderBook? orderBook))
                    return null;

                Order? buyOrder = orderBook.GetHighestBid();
                Order? sellOrder = orderBook.GetLowestAsk();

                if (buyOrder == null || sellOrder == null)
                    return null;

                if (buyOrder.LimitPrice!.Value < sellOrder.LimitPrice!.Value)
                    return null;

                decimal executionPrice = buyOrder.CreatedAt < sellOrder.CreatedAt ? buyOrder.LimitPrice.Value: sellOrder.CreatedAt < buyOrder.CreatedAt ? sellOrder.LimitPrice.Value : buyOrder.OrderId < sellOrder.OrderId ? buyOrder.LimitPrice.Value : sellOrder.LimitPrice.Value;
                int buyRemaining = buyOrder.Quantity - buyOrder.FilledQuantity;
                int sellRemaining = sellOrder.Quantity - sellOrder.FilledQuantity;
                int matchedQuantity = Math.Min(buyRemaining, sellRemaining);

                if (matchedQuantity <= 0)
                    throw new InvalidOperationException("Order contains no remaining quantity.");

                return new ProposedMatch(
                    buyOrder.OrderId,
                    sellOrder.OrderId,
                    instrumentId,
                    matchedQuantity,
                    executionPrice
                );
            }
        }
        public void ConfirmMatch(ProposedMatch match)
        {
            ArgumentNullException.ThrowIfNull(match);

            lock (orderMatchingEngineLock)
            {
                // 1. Find the order book.
                if (!OrderBooks.TryGetValue(match.InstrumentId,
                    out OrderBook? orderBook))
                    throw new InvalidOperationException(
                        "Order book not found.");

                // 2. Get the current highest-priority orders.
                Order? buyOrder = orderBook.GetHighestBid();
                Order? sellOrder = orderBook.GetLowestAsk();

                if (buyOrder == null || sellOrder == null)
                    throw new InvalidOperationException(
                        "No executable orders found.");

                // 3. Verify that the proposed orders are still
                // the orders at the top of the book.
                if (buyOrder.OrderId != match.BuyOrderId ||
                    sellOrder.OrderId != match.SellOrderId)
                    throw new InvalidOperationException(
                        "Proposed match is stale.");

                // 4. Validate that the orders remain executable.
                if (buyOrder.InstrumentId != match.InstrumentId ||
                    sellOrder.InstrumentId != match.InstrumentId ||
                    buyOrder.Side != OrderSide.Buy ||
                    sellOrder.Side != OrderSide.Sell)
                    throw new InvalidOperationException(
                        "Invalid order state.");

                if (buyOrder.Status is not
                        (OrderStatus.Pending or OrderStatus.PartiallyFilled) ||
                    sellOrder.Status is not
                        (OrderStatus.Pending or OrderStatus.PartiallyFilled))
                    throw new InvalidOperationException(
                        "Orders are no longer active.");

                // 5. Calculate current remaining quantities.
                int buyRemaining =
                    buyOrder.Quantity - buyOrder.FilledQuantity;

                int sellRemaining =
                    sellOrder.Quantity - sellOrder.FilledQuantity;

                int expectedQuantity =
                    Math.Min(buyRemaining, sellRemaining);

                if (expectedQuantity <= 0 ||
                    match.Quantity != expectedQuantity)
                    throw new InvalidOperationException(
                        "Proposed match quantity is stale.");

                // 6. Check that prices still cross.
                if (buyOrder.LimitPrice!.Value <
                    sellOrder.LimitPrice!.Value)
                    throw new InvalidOperationException(
                        "Orders no longer cross.");

                // Use the same pricing rule as ProposeMatch().
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

                // All validation is complete.
                // No expected failures should occur beyond this point.

                // 7. Apply confirmed filled quantities.
                buyOrder.FilledQuantity += match.Quantity;
                sellOrder.FilledQuantity += match.Quantity;

                // 8. Update BUY order.
                if (buyOrder.FilledQuantity == buyOrder.Quantity)
                {
                    buyOrder.Status = OrderStatus.Filled;
                    orderBook.PopBidQueue();
                }
                else
                {
                    buyOrder.Status = OrderStatus.PartiallyFilled;
                }

                // 9. Update SELL order.
                if (sellOrder.FilledQuantity == sellOrder.Quantity)
                {
                    sellOrder.Status = OrderStatus.Filled;
                    orderBook.PopAskQueue();
                }
                else
                {
                    sellOrder.Status = OrderStatus.PartiallyFilled;
                }
            }
        }

    }
}
