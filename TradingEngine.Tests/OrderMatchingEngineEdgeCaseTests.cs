
using System;
using System.Linq;
using Xunit;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Services;

namespace TradingEngine.Tests
{
    public class OrderMatchingEngineEdgeCaseTests
    {
        private readonly OrderMatchingEngine _engine = new();

        private static Order CreateOrder(
            int instrumentId,
            OrderSide side,
            int quantity,
            decimal price)
        {
            return new Order
            {
                AccountId = 1,
                InstrumentId = instrumentId,
                Side = side,
                OrderType = OrderType.Limit,
                Quantity = quantity,
                LimitPrice = price
            };
        }

        [Fact]
        public void SamePriceOrders_ShouldMatchEarlierOrderFirst()
        {
            var buy1 = _engine.AddOrder(
                CreateOrder(1, OrderSide.Buy, 10, 100m));

            var buy2 = _engine.AddOrder(
                CreateOrder(1, OrderSide.Buy, 10, 100m));

            var sell = _engine.AddOrder(
                CreateOrder(1, OrderSide.Sell, 10, 100m));

            var executions = _engine.LimitOrderMatcher(1);

            Assert.Single(executions);
            Assert.Equal(buy1.OrderId, executions[0].BuyOrderId);
            Assert.Equal(sell.OrderId, executions[0].SellOrderId);

            Assert.Equal(10, _engine.GetOrderById(buy1.OrderId)!.FilledQuantity);
            Assert.Equal(0, _engine.GetOrderById(buy2.OrderId)!.FilledQuantity);

            Assert.Equal(OrderStatus.Filled, _engine.GetOrderById(buy1.OrderId)!.Status);
            Assert.Equal(OrderStatus.Pending, _engine.GetOrderById(buy2.OrderId)!.Status);
        }

        [Fact]
        public void PartiallyFilledOrder_CancelRemainingQuantity()
        {
            var buy = _engine.AddOrder(
                CreateOrder(1, OrderSide.Buy, 100, 100m));

            var sell1 = _engine.AddOrder(
                CreateOrder(1, OrderSide.Sell, 40, 100m));

            var firstExecutions = _engine.LimitOrderMatcher(1);

            Assert.Single(firstExecutions);
            Assert.Equal(40, _engine.GetOrderById(buy.OrderId)!.FilledQuantity);
            Assert.Equal(OrderStatus.PartiallyFilled, _engine.GetOrderById(buy.OrderId)!.Status);

            bool cancelled = _engine.CancelOrder(buy.OrderId);

            Assert.True(cancelled);
            Assert.Equal(OrderStatus.Cancelled, _engine.GetOrderById(buy.OrderId)!.Status);

            var sell2 = _engine.AddOrder(
                CreateOrder(1, OrderSide.Sell, 60, 100m));

            var secondExecutions = _engine.LimitOrderMatcher(1);

            Assert.Empty(secondExecutions);
            Assert.Equal(40, _engine.GetOrderById(buy.OrderId)!.FilledQuantity);
            Assert.Equal(0, _engine.GetOrderById(sell2.OrderId)!.FilledQuantity);
        }

        [Fact]
        public void InsufficientLiquidity_ShouldPartiallyFillOrder()
        {
            var buy = _engine.AddOrder(
                CreateOrder(1, OrderSide.Buy, 100, 100m));

            var sell = _engine.AddOrder(
                CreateOrder(1, OrderSide.Sell, 30, 100m));

            var executions = _engine.LimitOrderMatcher(1);

            Assert.Single(executions);
            Assert.Equal(30, executions[0].ExecutedQuantity);

            // Retrieve updated order snapshots
            Order updatedBuy = _engine.GetOrderById(buy.OrderId)!;
            Order updatedSell = _engine.GetOrderById(sell.OrderId)!;

            Assert.Equal(30, updatedBuy.FilledQuantity);
            Assert.Equal(30, updatedSell.FilledQuantity);

            Assert.Equal(OrderStatus.PartiallyFilled, updatedBuy.Status);
            Assert.Equal(OrderStatus.Filled, updatedSell.Status);

            Assert.Equal(70, updatedBuy.Quantity - updatedBuy.FilledQuantity);
        }

        [Fact]
        public void NonCrossingOrders_ShouldNotExecute()
        {
            var buy = _engine.AddOrder(
                CreateOrder(1, OrderSide.Buy, 10, 100m));

            var sell = _engine.AddOrder(
                CreateOrder(1, OrderSide.Sell, 10, 105m));

            var executions = _engine.LimitOrderMatcher(1);

            Assert.Empty(executions);

            Assert.Equal(0, _engine.GetOrderById(buy.OrderId)!.FilledQuantity);
            Assert.Equal(0, _engine.GetOrderById(sell.OrderId)!.FilledQuantity);

            Assert.Equal(OrderStatus.Pending, _engine.GetOrderById(buy.OrderId)!.Status);
            Assert.Equal(OrderStatus.Pending, _engine.GetOrderById(sell.OrderId)!.Status);
        }

        [Fact]
        public void DifferentInstruments_ShouldNeverMatch()
        {
            var aaplBuy = _engine.AddOrder(
                CreateOrder(1, OrderSide.Buy, 10, 150m));

            var msftSell = _engine.AddOrder(
                CreateOrder(2, OrderSide.Sell, 10, 140m));

            var aaplExecutions = _engine.LimitOrderMatcher(1);
            var msftExecutions = _engine.LimitOrderMatcher(2);

            Assert.Empty(aaplExecutions);
            Assert.Empty(msftExecutions);

            Assert.Equal(0, _engine.GetOrderById(aaplBuy.OrderId)!.FilledQuantity);
            Assert.Equal(0, _engine.GetOrderById(msftSell.OrderId)!.FilledQuantity);
        }

        [Theory]
        [InlineData(0, 100)]
        [InlineData(-10, 100)]
        [InlineData(10, 0)]
        [InlineData(10, -100)]
        public void InvalidQuantityOrPrice_ShouldRejectOrder(
            int quantity,
            int price)
        {
            var order = CreateOrder(
                1, OrderSide.Buy, quantity, price);

            Assert.Throws<ArgumentException>(
                () => _engine.AddOrder(order));
        }

        [Fact]
        public void InvalidInstrumentId_ShouldRejectOrder()
        {
            var order = CreateOrder(
                0, OrderSide.Buy, 10, 100m);

            Assert.Throws<ArgumentException>(
                () => _engine.AddOrder(order));
        }

        [Fact]
        public void InvalidAccountId_ShouldRejectOrder()
        {
            var order = CreateOrder(
                1, OrderSide.Buy, 10, 100m);

            order.AccountId = 0;

            Assert.Throws<ArgumentException>(
                () => _engine.AddOrder(order));
        }

        [Fact]
        public void InvalidOrderSide_ShouldRejectOrder()
        {
            var order = CreateOrder(
                1, (OrderSide)999, 10, 100m);

            Assert.Throws<ArgumentException>(
                () => _engine.AddOrder(order));
        }

        [Fact]
        public void AlreadyFilledNewOrder_ShouldRejectOrder()
        {
            var order = CreateOrder(
                1, OrderSide.Buy, 10, 100m);

            order.FilledQuantity = 5;

            Assert.Throws<ArgumentException>(
                () => _engine.AddOrder(order));
        }

        [Fact]
        public void UnsupportedOrderType_ShouldRejectOrder()
        {
            var order = CreateOrder(
                1, OrderSide.Buy, 10, 100m);

            order.OrderType = (OrderType)999;

            Assert.Throws<ArgumentException>(
                () => _engine.AddOrder(order));
        }

        [Fact]
        public void PartiallyFilledOrder_ShouldRetainFIFOPosition()
        {
            var buy1 = _engine.AddOrder(
                CreateOrder(1, OrderSide.Buy, 100, 100m));

            var buy2 = _engine.AddOrder(
                CreateOrder(1, OrderSide.Buy, 50, 100m));

            var sell1 = _engine.AddOrder(
                CreateOrder(1, OrderSide.Sell, 40, 100m));

            var firstExecutions = _engine.LimitOrderMatcher(1);

            Assert.Single(firstExecutions);
            Assert.Equal(buy1.OrderId, firstExecutions[0].BuyOrderId);
            Assert.Equal(40, _engine.GetOrderById(buy1.OrderId)!.FilledQuantity);

            var sell2 = _engine.AddOrder(
                CreateOrder(1, OrderSide.Sell, 60, 100m));

            var secondExecutions = _engine.LimitOrderMatcher(1);

            Assert.Single(secondExecutions);
            Assert.Equal(buy1.OrderId, secondExecutions[0].BuyOrderId);

            Assert.Equal(100, _engine.GetOrderById(buy1.OrderId)!.FilledQuantity);
            Assert.Equal(0, _engine.GetOrderById(buy2.OrderId)!.FilledQuantity);
        }
    }
}
