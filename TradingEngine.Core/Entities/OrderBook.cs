
using System;
using System.Collections.Generic;
using System.Reflection.Metadata.Ecma335;
using TradingEngine.Core.Enums;

namespace TradingEngine.Core.Entities
{
    public class OrderBook
    {
        public int InstrumentId { get; }

        private readonly PriorityQueue<Order, (decimal Price, long Sequence)> Bids;
        private readonly PriorityQueue<Order, (decimal Price, long Sequence)> Asks;

        public OrderBook(int instrumentId)
        {
            InstrumentId = instrumentId;

            Bids = new PriorityQueue<Order, (decimal Price, long Sequence)>(
                Comparer<(decimal Price, long Sequence)>.Create((a, b) =>
                {
                    int priceComparison = b.Price.CompareTo(a.Price);

                    if (priceComparison != 0)
                        return priceComparison;

                    return a.Sequence.CompareTo(b.Sequence);
                }));

            Asks = new PriorityQueue<Order, (decimal Price, long Sequence)>(
                Comparer<(decimal Price, long Sequence)>.Create((a, b) =>
                {
                    int priceComparison = a.Price.CompareTo(b.Price);

                    if (priceComparison != 0)
                        return priceComparison;

                    return a.Sequence.CompareTo(b.Sequence);
                }));
        }

        public void PushBidQueue(Order order, long sequence)
        {
            Bids.Enqueue(order, (order.LimitPrice!.Value, sequence));
        }

        public void PushAskQueue(Order order, long sequence)
        {
            Asks.Enqueue(order, (order.LimitPrice!.Value, sequence));
        }

        public Order? GetHighestBid()
        {
            while (Bids.TryPeek(out Order? order, out _))
            {
                if (order!.Status != OrderStatus.Cancelled)
                    return order;
                Bids.Dequeue();
            }
            return null;
        }

        public Order? GetLowestAsk()
        {
            while (Asks.TryPeek(out Order? order, out _))
            {
                if (order!.Status != OrderStatus.Cancelled)
                    return order;
                Asks.Dequeue();
            }
            return null;
        }

        public void PopBidQueue()
        {
            Bids.TryDequeue(out _, out _);
        }

        public void PopAskQueue()
        {
            Asks.TryDequeue(out _, out _);
        }

        public int GetBidQueueSize()
        {
            return Bids.Count;
        }

        public int GetAskQueueSize()
        {
            return Asks.Count;
        }
    }
}
