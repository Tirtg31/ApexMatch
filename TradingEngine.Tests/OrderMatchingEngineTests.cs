
using System;
using System.Linq;
using Xunit;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Services;

namespace TradingEngine.Tests
{
    public class OrderMatchingEngineTests
    {
        private readonly OrderMatchingEngine _engine = new();

        // Helper method for creating test orders
        private static Order CreateOrder(
            int instrumentId,
            OrderSide side,
            int quantity,
            decimal price,
            int accountId = 1)
        {
            return new Order
            {
                InstrumentId = instrumentId,
                AccountId = accountId,
                Side = side,
                Quantity = quantity,
                LimitPrice = price,
                FilledQuantity = 0
            };
        }

        [Fact]
        public void AddOrder_ShouldAssignIdAndPendingStatus()
        {
            // Arrange
            Order buy = CreateOrder(101, OrderSide.Buy, 100, 105m);

            // Act
            Order result = _engine.AddOrder(buy);

            // Assert
            Assert.Equal(1, result.OrderId);
            Assert.Equal(OrderStatus.Pending, result.Status);
            Assert.Equal(0, result.FilledQuantity);
            Order stored = _engine.GetOrderById(result.OrderId)!;
            Assert.NotSame(buy, stored);
            Assert.NotSame(result, stored);
            Assert.Equal(result.OrderId, stored.OrderId);
            Assert.Equal(result.Quantity, stored.Quantity);
            Assert.Equal(result.LimitPrice, stored.LimitPrice);
        }

        [Fact]
        public void MatchingOrders_ShouldCreateExecution()
        {
            // Arrange
            Order buy = _engine.AddOrder(
                CreateOrder(101, OrderSide.Buy, 100, 105m));

            Order sell = _engine.AddOrder(
                CreateOrder(101, OrderSide.Sell, 100, 100m));

            // Act
            var executions = _engine.LimitOrderMatcher(101);

            // Assert
            Assert.Single(executions);

            Execution execution = executions[0];

            Assert.Equal(buy.OrderId, execution.BuyOrderId);
            Assert.Equal(sell.OrderId, execution.SellOrderId);
            Assert.Equal(100, execution.ExecutedQuantity);
            Assert.Equal(105m, execution.ExecutedPrice);

            Assert.Equal(OrderStatus.Filled, _engine.GetOrderById(buy.OrderId)!.Status);
            Assert.Equal(OrderStatus.Filled, _engine.GetOrderById(sell.OrderId)!.Status);
        }

        [Fact]
        public void NonCrossingOrders_ShouldNotMatch()
        {
            // Arrange
            Order buy = _engine.AddOrder(
                CreateOrder(101, OrderSide.Buy, 100, 95m));

            Order sell = _engine.AddOrder(
                CreateOrder(101, OrderSide.Sell, 100, 100m));

            // Act
            var executions = _engine.LimitOrderMatcher(101);

            // Assert
            Assert.Empty(executions);
            Assert.Equal(0, _engine.GetOrderById(buy.OrderId)!.FilledQuantity);
            Assert.Equal(0, _engine.GetOrderById(sell.OrderId)!.FilledQuantity);

            Assert.Equal(OrderStatus.Pending, _engine.GetOrderById(buy.OrderId)!.Status);
            Assert.Equal(OrderStatus.Pending, _engine.GetOrderById(sell.OrderId)!.Status);
        }

        [Fact]
        public void PartialFill_ShouldLeaveRemainingQuantity()
        {
            // Arrange
            Order buy = _engine.AddOrder(
                CreateOrder(101, OrderSide.Buy, 100, 105m));

            Order sell = _engine.AddOrder(
                CreateOrder(101, OrderSide.Sell, 40, 100m));

            // Act
            var executions = _engine.LimitOrderMatcher(101);

            // Assert
            Assert.Single(executions);
            Assert.Equal(40, executions[0].ExecutedQuantity);

            Assert.Equal(40, _engine.GetOrderById(buy.OrderId)!.FilledQuantity);
            Assert.Equal(60, buy.Quantity - _engine.GetOrderById(buy.OrderId)!.FilledQuantity);

            Assert.Equal(OrderStatus.PartiallyFilled, _engine.GetOrderById(buy.OrderId)!.Status);
            Assert.Equal(OrderStatus.Filled, _engine.GetOrderById(sell.OrderId)!.Status);
        }

        [Fact]
        public void OneBuyOrder_ShouldMatchMultipleSellOrders()
        {
            // Arrange
            Order buy = _engine.AddOrder(
                CreateOrder(101, OrderSide.Buy, 100, 105m));

            Order sell1 = _engine.AddOrder(
                CreateOrder(101, OrderSide.Sell, 30, 100m));

            Order sell2 = _engine.AddOrder(
                CreateOrder(101, OrderSide.Sell, 70, 102m));

            // Act
            var executions = _engine.LimitOrderMatcher(101);

            // Assert
            Assert.Equal(2, executions.Count);
            Assert.Equal(100, executions.Sum(e => e.ExecutedQuantity));

            Assert.Equal(100, _engine.GetOrderById(buy.OrderId)!.FilledQuantity);
            Assert.Equal(OrderStatus.Filled, _engine.GetOrderById(buy.OrderId)!.Status);

            Assert.Equal(OrderStatus.Filled, _engine.GetOrderById(sell1.OrderId)!.Status);
            Assert.Equal(OrderStatus.Filled, _engine.GetOrderById(sell2.OrderId)!.Status);
        }

        [Fact]
        public void HigherBid_ShouldMatchBeforeLowerBid()
        {
            // Arrange
            Order lowBid = _engine.AddOrder(
                CreateOrder(101, OrderSide.Buy, 10, 100m));

            Order highBid = _engine.AddOrder(
                CreateOrder(101, OrderSide.Buy, 10, 105m));

            _engine.AddOrder(
                CreateOrder(101, OrderSide.Sell, 10, 95m));

            // Act
            var executions = _engine.LimitOrderMatcher(101);

            // Assert
            Assert.Single(executions);
            Assert.Equal(highBid.OrderId, executions[0].BuyOrderId);

            Assert.Equal(OrderStatus.Filled, _engine.GetOrderById(highBid.OrderId)!.Status);
            Assert.Equal(OrderStatus.Pending, _engine.GetOrderById(lowBid.OrderId)!.Status);
        }

        [Fact]
        public void LowerAsk_ShouldMatchBeforeHigherAsk()
        {
            // Arrange
            Order highAsk = _engine.AddOrder(
                CreateOrder(101, OrderSide.Sell, 10, 103m));

            Order lowAsk = _engine.AddOrder(
                CreateOrder(101, OrderSide.Sell, 10, 100m));

            _engine.AddOrder(
                CreateOrder(101, OrderSide.Buy, 10, 105m));

            // Act
            var executions = _engine.LimitOrderMatcher(101);

            // Assert
            Assert.Single(executions);
            Assert.Equal(lowAsk.OrderId, executions[0].SellOrderId);

            Assert.Equal(OrderStatus.Filled, _engine.GetOrderById(lowAsk.OrderId)!.Status);
            Assert.Equal(OrderStatus.Pending, _engine.GetOrderById(highAsk.OrderId)!.Status);
        }

        [Fact]
        public void SamePriceOrders_ShouldFollowFIFO()
        {
            // Arrange
            Order firstBuy = _engine.AddOrder(
                CreateOrder(101, OrderSide.Buy, 10, 100m));

            Order secondBuy = _engine.AddOrder(
                CreateOrder(101, OrderSide.Buy, 10, 100m));

            _engine.AddOrder(
                CreateOrder(101, OrderSide.Sell, 10, 100m));

            // Act
            var executions = _engine.LimitOrderMatcher(101);

            // Assert
            Assert.Single(executions);

            Assert.Equal(firstBuy.OrderId, executions[0].BuyOrderId);
            Assert.Equal(OrderStatus.Filled, _engine.GetOrderById(firstBuy.OrderId)!.Status);
            Assert.Equal(OrderStatus.Pending, _engine.GetOrderById(secondBuy.OrderId)!.Status);
        }

        [Fact]
        public void DifferentInstruments_ShouldNotMatch()
        {
            // Arrange
            _engine.AddOrder(
                CreateOrder(101, OrderSide.Buy, 100, 105m));

            _engine.AddOrder(
                CreateOrder(102, OrderSide.Sell, 100, 100m));

            // Act
            var executions101 = _engine.LimitOrderMatcher(101);
            var executions102 = _engine.LimitOrderMatcher(102);

            // Assert
            Assert.Empty(executions101);
            Assert.Empty(executions102);
        }

        [Fact]
        public void FilledOrders_ShouldNotExecuteAgain()
        {
            // Arrange
            _engine.AddOrder(
                CreateOrder(101, OrderSide.Buy, 100, 105m));

            _engine.AddOrder(
                CreateOrder(101, OrderSide.Sell, 100, 100m));

            // Act
            var firstResult = _engine.LimitOrderMatcher(101);
            var secondResult = _engine.LimitOrderMatcher(101);

            // Assert
            Assert.Single(firstResult);
            Assert.Empty(secondResult);
        }

        [Fact]
        public void EmptyOrderBook_ShouldReturnNoExecutions()
        {
            // Act
            var executions = _engine.LimitOrderMatcher(999);

            // Assert
            Assert.Empty(executions);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-10)]
        public void InvalidQuantity_ShouldThrowException(int quantity)
        {
            // Arrange
            Order order = CreateOrder(
                101, OrderSide.Buy, quantity, 100m);

            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
                _engine.AddOrder(order));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-100)]
        public void InvalidPrice_ShouldThrowException(int price)
        {
            // Arrange
            Order order = CreateOrder(
                101, OrderSide.Buy, 10, price);

            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
                _engine.AddOrder(order));
        }

        [Fact]
        public void OrderIds_ShouldBeUnique()
        {
            // Arrange & Act
            Order order1 = _engine.AddOrder(
                CreateOrder(101, OrderSide.Buy, 10, 100m));

            Order order2 = _engine.AddOrder(
                CreateOrder(101, OrderSide.Sell, 10, 105m));

            // Assert
            Assert.NotEqual(order1.OrderId, order2.OrderId);
        }

        [Fact]
        public void ExecutionIds_ShouldBeUnique()
        {
            // Arrange
            _engine.AddOrder(
                CreateOrder(101, OrderSide.Buy, 10, 105m));

            _engine.AddOrder(
                CreateOrder(101, OrderSide.Sell, 5, 100m));

            _engine.AddOrder(
                CreateOrder(101, OrderSide.Sell, 5, 101m));

            // Act
            var executions = _engine.LimitOrderMatcher(101);

            // Assert
            Assert.Equal(2, executions.Count);

            Assert.NotEqual(
                executions[0].ExecutionId,
                executions[1].ExecutionId);
        }

        #region Cancellation Tests

        [Fact]
        public void CancelPendingOrder_ShouldReturnTrue()
        {
            // Arrange
            Order buy = _engine.AddOrder(
                CreateOrder(101, OrderSide.Buy, 100, 150m));

            // Act
            bool result = _engine.CancelOrder(buy.OrderId);

            // Assert
            Assert.True(result);
            Assert.Equal(OrderStatus.Cancelled, _engine.GetOrderById(buy.OrderId)!.Status);
            Assert.Equal(0, _engine.GetOrderById(buy.OrderId)!.FilledQuantity);
        }

        [Fact]
        public void CancelFilledOrder_ShouldReturnFalse()
        {
            // Arrange
            Order buy = _engine.AddOrder(
                CreateOrder(101, OrderSide.Buy, 100, 150m));

            Order sell = _engine.AddOrder(
                CreateOrder(101, OrderSide.Sell, 100, 145m));

            _engine.LimitOrderMatcher(101);

            // Act
            bool result = _engine.CancelOrder(buy.OrderId);

            // Assert
            Assert.False(result);
            Assert.Equal(OrderStatus.Filled, _engine.GetOrderById(buy.OrderId)!.Status);
            Assert.Equal(100, _engine.GetOrderById(buy.OrderId)!.FilledQuantity);
        }

        [Fact]
        public void CancelPartiallyFilledOrder_ShouldPreserveFilledQuantity()
        {
            // Arrange
            Order buy = _engine.AddOrder(
                CreateOrder(101, OrderSide.Buy, 100, 150m));

            _engine.AddOrder(
                CreateOrder(101, OrderSide.Sell, 40, 145m));

            _engine.LimitOrderMatcher(101);

            // Verify partial fill
            Assert.Equal(OrderStatus.PartiallyFilled, _engine.GetOrderById(buy.OrderId)!.Status);
            Assert.Equal(40, _engine.GetOrderById(buy.OrderId)!.FilledQuantity);

            // Act
            bool result = _engine.CancelOrder(buy.OrderId);

            // Assert
            Assert.True(result);
            Assert.Equal(OrderStatus.Cancelled, _engine.GetOrderById(buy.OrderId)!.Status);
            Assert.Equal(40, _engine.GetOrderById(buy.OrderId)!.FilledQuantity);

            // Add another sell order
            _engine.AddOrder(
                CreateOrder(101, OrderSide.Sell, 60, 145m));

            var executions = _engine.LimitOrderMatcher(101);

            // Cancelled buy order should not execute again
            Assert.Empty(executions);
            Assert.Equal(40, _engine.GetOrderById(buy.OrderId)!.FilledQuantity);
        }

        [Fact]
        public void CancelAlreadyCancelledOrder_ShouldReturnFalse()
        {
            // Arrange
            Order buy = _engine.AddOrder(
                CreateOrder(101, OrderSide.Buy, 100, 150m));

            _engine.CancelOrder(buy.OrderId);

            // Act
            bool result = _engine.CancelOrder(buy.OrderId);

            // Assert
            Assert.False(result);
            Assert.Equal(OrderStatus.Cancelled, _engine.GetOrderById(buy.OrderId)!.Status);
        }

        [Fact]
        public void CancelNonExistingOrder_ShouldReturnFalse()
        {
            // Act
            bool result = _engine.CancelOrder(999);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void CancelledHighestBid_ShouldSkipAndMatchNextValidBid()
        {
            // Arrange
            Order highestBid = _engine.AddOrder(
                CreateOrder(101, OrderSide.Buy, 10, 150m));

            Order secondBid = _engine.AddOrder(
                CreateOrder(101, OrderSide.Buy, 10, 145m));

            // Cancel highest-priority bid
            _engine.CancelOrder(highestBid.OrderId);

            Order sell = _engine.AddOrder(
                CreateOrder(101, OrderSide.Sell, 10, 140m));

            // Act
            var executions = _engine.LimitOrderMatcher(101);

            // Assert
            Assert.Single(executions);

            Assert.Equal(secondBid.OrderId, executions[0].BuyOrderId);
            Assert.Equal(sell.OrderId, executions[0].SellOrderId);

            Assert.Equal(145m, executions[0].ExecutedPrice);

            Assert.Equal(OrderStatus.Cancelled, _engine.GetOrderById(highestBid.OrderId)!.Status);
            Assert.Equal(0, _engine.GetOrderById(highestBid.OrderId)!.FilledQuantity);

            Assert.Equal(OrderStatus.Filled, _engine.GetOrderById(secondBid.OrderId)!.Status);
            Assert.Equal(OrderStatus.Filled, _engine.GetOrderById(sell.OrderId)!.Status);
        }

        #endregion

    }
}
