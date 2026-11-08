
using System;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Services;
using Xunit;

namespace TradingEngine.Tests
{
    public class OrderServiceTests
    {
        private readonly AccountService _accountService;
        private readonly PositionService _positionService;
        private readonly OrderMatchingEngine _matchingEngine;
        private readonly OrderService _orderService;

        private const int InstrumentId = 1;

        public OrderServiceTests()
        {
            // Each test gets fresh, independent service instances.
            _accountService = new AccountService();
            _positionService = new PositionService();
            _matchingEngine = new OrderMatchingEngine();

            _orderService = new OrderService(
                _accountService,
                _matchingEngine,
                _positionService);
        }

        // Helper: Create an active trading account.
        private Account CreateAccount(decimal cash = 100000m)
        {
            return _accountService.CreateAccount(new Account
            {
                AccountName = "Test Trader",
                CashBalance = cash
            });
        }

        // Helper: Create a valid limit order.
        private Order CreateOrder(
            int accountId,
            OrderSide side,
            int quantity = 10,
            decimal price = 500m)
        {
            return new Order
            {
                AccountId = accountId,
                InstrumentId = InstrumentId,
                Side = side,
                OrderType = OrderType.Limit,
                Quantity = quantity,
                LimitPrice = price
            };
        }

        // ========================================================
        // CATEGORY 1: SUCCESSFUL ORDER PLACEMENT
        // ========================================================

        // TEST 01
        // Verify that placing a valid BUY order reserves the
        // required cash without deducting the total cash balance.
        [Fact]
        public void PlaceBuyOrder_ValidOrder_ReservesCash()
        {
            // Arrange
            Account account = CreateAccount();
            Order order = CreateOrder(
                account.AccountId, OrderSide.Buy, 10, 500m);

            // Act
            Order accepted = _orderService.PlaceBuyOrder(order);

            // Assert
            Assert.NotNull(accepted);
            Assert.True(accepted.OrderId > 0);
            Assert.Equal(OrderStatus.Pending, accepted.Status);

            Assert.Equal(100000m, account.CashBalance);
            Assert.Equal(5000m, account.ReservedCash);
            Assert.Equal(95000m, account.AvailableCash);
        }

        // TEST 02
        // Verify that placing a valid SELL order reserves shares
        // without reducing the seller's total owned quantity.
        [Fact]
        public void PlaceSellOrder_ValidOrder_ReservesShares()
        {
            // Arrange
            Account account = CreateAccount();

            _positionService.AddPosition(
                account.AccountId, InstrumentId, 100, 400m);

            Order order = CreateOrder(
                account.AccountId, OrderSide.Sell, 60, 500m);

            // Act
            Order accepted = _orderService.PlaceSellOrder(order);

            // Assert
            var position = _positionService.GetPosition(
                account.AccountId, InstrumentId);

            Assert.NotNull(accepted);
            Assert.True(accepted.OrderId > 0);
            Assert.Equal(OrderStatus.Pending, accepted.Status);

            Assert.NotNull(position);
            Assert.Equal(100, position.Quantity);
            Assert.Equal(60, position.ReservedQuantity);
            Assert.Equal(40, position.AvailableQuantity);
        }

        // TEST 03
        // Verify that a BUY order does not require the buyer to
        // already own shares of the instrument.
        [Fact]
        public void PlaceBuyOrder_NoExistingPosition_Succeeds()
        {
            // Arrange
            Account account = CreateAccount(10000m);

            Order order = CreateOrder(
                account.AccountId, OrderSide.Buy, 5, 200m);

            // Act
            Order accepted = _orderService.PlaceBuyOrder(order);

            // Assert
            Assert.NotNull(accepted);
            Assert.Equal(1000m, account.ReservedCash);
            Assert.Equal(9000m, account.AvailableCash);

            Assert.Null(_positionService.GetPosition(
                account.AccountId, InstrumentId));
        }

        // TEST 04
        // Verify that submitting a SELL order only reserves shares.
        // The seller must not receive cash until execution settles.
        [Fact]
        public void PlaceSellOrder_ValidOrder_DoesNotCreditCash()
        {
            // Arrange
            Account account = CreateAccount(1000m);

            _positionService.AddPosition(
                account.AccountId, InstrumentId, 50, 300m);

            Order order = CreateOrder(
                account.AccountId, OrderSide.Sell, 20, 500m);

            // Act
            _orderService.PlaceSellOrder(order);

            // Assert
            Assert.Equal(1000m, account.CashBalance);
            Assert.Equal(1000m, account.AvailableCash);
            Assert.Equal(0m, account.ReservedCash);

            Assert.Equal(20,
                _positionService.GetPosition(
                    account.AccountId, InstrumentId)!
                    .ReservedQuantity);
        }

        // ========================================================
        // CATEGORY 2: ACCOUNT VALIDATION
        // ========================================================

        // TEST 05
        // Verify that BUY orders from nonexistent accounts
        // are rejected before the order reaches the engine.
        [Fact]
        public void PlaceBuyOrder_AccountNotFound_ThrowsException()
        {
            // Arrange
            Order order = CreateOrder(999, OrderSide.Buy);

            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
                _orderService.PlaceBuyOrder(order));

            Assert.Equal(0, order.OrderId);
        }

        // TEST 06
        // Verify that SELL orders from nonexistent accounts
        // are rejected.
        [Fact]
        public void PlaceSellOrder_AccountNotFound_ThrowsException()
        {
            // Arrange
            Order order = CreateOrder(999, OrderSide.Sell);

            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
                _orderService.PlaceSellOrder(order));

            Assert.Equal(0, order.OrderId);
        }

        // TEST 07
        // Verify that an inactive account cannot submit BUY orders
        // and that its existing cash remains unchanged.
        [Fact]
        public void PlaceBuyOrder_InactiveAccount_ThrowsException()
        {
            // Arrange
            Account account = CreateAccount();
            account.IsActive = false;

            Order order = CreateOrder(
                account.AccountId, OrderSide.Buy);

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() =>
                _orderService.PlaceBuyOrder(order));

            Assert.Equal(0m, account.ReservedCash);
            Assert.Equal(100000m, account.AvailableCash);
        }

        // TEST 08
        // Verify that an inactive account cannot submit SELL orders.
        // Its shares must remain available.
        [Fact]
        public void PlaceSellOrder_InactiveAccount_ThrowsException()
        {
            // Arrange
            Account account = CreateAccount();
            account.IsActive = false;

            _positionService.AddPosition(
                account.AccountId, InstrumentId, 100, 400m);

            Order order = CreateOrder(
                account.AccountId, OrderSide.Sell);

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() =>
                _orderService.PlaceSellOrder(order));

            var position = _positionService.GetPosition(
                account.AccountId, InstrumentId);

            Assert.NotNull(position);
            Assert.Equal(0, position.ReservedQuantity);
            Assert.Equal(100, position.AvailableQuantity);
        }

        // ========================================================
        // CATEGORY 3: ORDER VALIDATION
        // ========================================================

        // TEST 09
        // Verify that passing null to PlaceBuyOrder results
        // in ArgumentNullException.
        [Fact]
        public void PlaceBuyOrder_NullOrder_ThrowsException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                _orderService.PlaceBuyOrder(null!));
        }

        // TEST 10
        // Verify that passing null to PlaceSellOrder results
        // in ArgumentNullException.
        [Fact]
        public void PlaceSellOrder_NullOrder_ThrowsException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                _orderService.PlaceSellOrder(null!));
        }

        // TEST 11
        // Verify that BUY submission rejects:
        // - Wrong order side
        // - Unsupported order type
        // - Zero quantity
        // - Invalid limit price
        // - Invalid instrument ID
        // - Invalid account ID
        //
        // Each InlineData entry runs as a separate test case.
        [Theory]
        [InlineData(OrderSide.Sell, OrderType.Limit, 10, 500, 1, 1)]
        [InlineData(OrderSide.Buy, OrderType.Market, 10, 500, 1, 1)]
        [InlineData(OrderSide.Buy, OrderType.Limit, 0, 500, 1, 1)]
        [InlineData(OrderSide.Buy, OrderType.Limit, 10, 0, 1, 1)]
        [InlineData(OrderSide.Buy, OrderType.Limit, 10, 500, 0, 1)]
        [InlineData(OrderSide.Buy, OrderType.Limit, 10, 500, 1, 0)]
        public void PlaceBuyOrder_InvalidOrder_ThrowsException(
            OrderSide side,
            OrderType type,
            int quantity,
            int price,
            int instrumentId,
            int accountId)
        {
            // Arrange
            Account account = CreateAccount();

            Order order = new Order
            {
                AccountId = accountId == 0 ? 0 : account.AccountId,
                InstrumentId = instrumentId,
                Side = side,
                OrderType = type,
                Quantity = quantity,
                LimitPrice = price
            };

            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
                _orderService.PlaceBuyOrder(order));

            Assert.Equal(0m, account.ReservedCash);
            Assert.Equal(100000m, account.AvailableCash);
        }

        // TEST 12
        // Verify that SELL submission rejects:
        // - Wrong order side
        // - Unsupported order type
        // - Zero quantity
        // - Invalid limit price
        // - Invalid instrument ID
        // - Invalid account ID
        //
        // Invalid sell orders must never reserve shares.
        [Theory]
        [InlineData(OrderSide.Buy, OrderType.Limit, 10, 500, 1, 1)]
        [InlineData(OrderSide.Sell, OrderType.Market, 10, 500, 1, 1)]
        [InlineData(OrderSide.Sell, OrderType.Limit, 0, 500, 1, 1)]
        [InlineData(OrderSide.Sell, OrderType.Limit, 10, 0, 1, 1)]
        [InlineData(OrderSide.Sell, OrderType.Limit, 10, 500, 0, 1)]
        [InlineData(OrderSide.Sell, OrderType.Limit, 10, 500, 1, 0)]
        public void PlaceSellOrder_InvalidOrder_ThrowsException(
            OrderSide side,
            OrderType type,
            int quantity,
            int price,
            int instrumentId,
            int accountId)
        {
            // Arrange
            Account account = CreateAccount();

            _positionService.AddPosition(
                account.AccountId, InstrumentId, 100, 400m);

            Order order = new Order
            {
                AccountId = accountId == 0 ? 0 : account.AccountId,
                InstrumentId = instrumentId,
                Side = side,
                OrderType = type,
                Quantity = quantity,
                LimitPrice = price
            };

            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
                _orderService.PlaceSellOrder(order));

            var position = _positionService.GetPosition(
                account.AccountId, InstrumentId);

            Assert.NotNull(position);
            Assert.Equal(0, position.ReservedQuantity);
            Assert.Equal(100, position.AvailableQuantity);
        }

        // ========================================================
        // CATEGORY 4: RESOURCES AND MULTIPLE ORDERS
        // ========================================================

        // TEST 13
        // Verify that a BUY order exceeding available cash
        // is rejected without reserving any money.
        [Fact]
        public void PlaceBuyOrder_InsufficientFunds_ThrowsException()
        {
            // Arrange
            Account account = CreateAccount(1000m);

            Order order = CreateOrder(
                account.AccountId, OrderSide.Buy, 10, 500m);

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() =>
                _orderService.PlaceBuyOrder(order));

            Assert.Equal(1000m, account.CashBalance);
            Assert.Equal(1000m, account.AvailableCash);
            Assert.Equal(0m, account.ReservedCash);
            Assert.Equal(0, order.OrderId);
        }

        // TEST 14
        // Verify that SELL orders cannot reserve more shares
        // than the seller currently owns.
        [Fact]
        public void PlaceSellOrder_InsufficientShares_ThrowsException()
        {
            // Arrange
            Account account = CreateAccount();

            _positionService.AddPosition(
                account.AccountId, InstrumentId, 50, 400m);

            Order order = CreateOrder(
                account.AccountId, OrderSide.Sell, 80, 500m);

            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
                _orderService.PlaceSellOrder(order));

            var position = _positionService.GetPosition(
                account.AccountId, InstrumentId);

            Assert.NotNull(position);
            Assert.Equal(50, position.AvailableQuantity);
            Assert.Equal(0, position.ReservedQuantity);
            Assert.Equal(0, order.OrderId);
        }

        // TEST 15
        // Verify that multiple BUY orders share the account's
        // available cash and cannot reserve the same funds twice.
        [Fact]
        public void PlaceBuyOrder_MultipleOrders_PreventsOverReservation()
        {
            // Arrange
            Account account = CreateAccount(10000m);

            Order first = CreateOrder(
                account.AccountId, OrderSide.Buy, 15, 500m);

            Order second = CreateOrder(
                account.AccountId, OrderSide.Buy, 10, 500m);

            // Act
            Order acceptedFirst = _orderService.PlaceBuyOrder(first);

            // First order reserves 7500.
            // Only 2500 remains, so second order must fail.
            Assert.Throws<InvalidOperationException>(() =>
                _orderService.PlaceBuyOrder(second));

            // Retrieve latest account state
            Account updatedAccount =
                _accountService.GetAccountById(account.AccountId)!;

            // Assert account balances
            Assert.Equal(7500m, updatedAccount.ReservedCash);
            Assert.Equal(2500m, updatedAccount.AvailableCash);
            Assert.Equal(10000m, updatedAccount.CashBalance);

            // Assert order IDs
            Assert.True(acceptedFirst.OrderId > 0);
            Assert.Equal(0, second.OrderId);
        }

        // TEST 16
        // Verify that multiple SELL orders cannot collectively
        // reserve more shares than the account owns.
        [Fact]
        public void PlaceSellOrder_MultipleOrders_PreventsOverReservation()
        {
            // Arrange
            Account account = CreateAccount();

            _positionService.AddPosition(
                account.AccountId, InstrumentId, 100, 400m);

            Order first = CreateOrder(
                account.AccountId, OrderSide.Sell, 70, 500m);

            Order second = CreateOrder(
                account.AccountId, OrderSide.Sell, 40, 500m);

            // Act
            Order acceptedFirst = _orderService.PlaceSellOrder(first);

            Assert.Throws<ArgumentException>(() =>
                _orderService.PlaceSellOrder(second));

            // Retrieve latest position snapshot
            Position position = _positionService.GetPosition(
                account.AccountId, InstrumentId)!;

            // Assert position quantities
            Assert.Equal(100, position.Quantity);
            Assert.Equal(70, position.ReservedQuantity);
            Assert.Equal(30, position.AvailableQuantity);

            // Assert order IDs
            Assert.True(acceptedFirst.OrderId > 0);
            Assert.Equal(0, second.OrderId);

            // Verify accepted order exists in matching engine
            Order storedOrder = _matchingEngine
                .GetOrderById(acceptedFirst.OrderId)!;

            Assert.Equal(70, storedOrder.Quantity);
            Assert.Equal(OrderStatus.Pending, storedOrder.Status);
        }

        // ========================================================
        // CATEGORY 5: RESERVATION ROLLBACK
        // ========================================================

        // TEST 17
        // Verify that if the matching engine rejects a BUY order
        // after cash was reserved, the full reservation is released.
        //
        // FilledQuantity = 1 intentionally triggers rejection
        // inside OrderMatchingEngine.AddOrder().
        [Fact]
        public void PlaceBuyOrder_MatchingEngineFails_ReleasesCash()
        {
            // Arrange
            Account account = CreateAccount(10000m);

            Order order = CreateOrder(
                account.AccountId, OrderSide.Buy, 10, 500m);

            order.FilledQuantity = 1;

            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
                _orderService.PlaceBuyOrder(order));

            Assert.Equal(10000m, account.CashBalance);
            Assert.Equal(10000m, account.AvailableCash);
            Assert.Equal(0m, account.ReservedCash);
            Assert.Equal(0, order.OrderId);
        }

        // TEST 18
        // Verify that if the matching engine rejects a SELL order
        // after shares were reserved, all reserved shares are released.
        [Fact]
        public void PlaceSellOrder_MatchingEngineFails_ReleasesShares()
        {
            // Arrange
            Account account = CreateAccount();

            _positionService.AddPosition(
                account.AccountId, InstrumentId, 100, 400m);

            Order order = CreateOrder(
                account.AccountId, OrderSide.Sell, 60, 500m);

            order.FilledQuantity = 1;

            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
                _orderService.PlaceSellOrder(order));

            var position = _positionService.GetPosition(
                account.AccountId, InstrumentId);

            Assert.NotNull(position);
            Assert.Equal(100, position.Quantity);
            Assert.Equal(0, position.ReservedQuantity);
            Assert.Equal(100, position.AvailableQuantity);
            Assert.Equal(0, order.OrderId);
        }

        // ========================================================
        // CATEGORY 6: ORDER REGISTRATION AND ISOLATION
        // ========================================================

        // TEST 19
        // Verify that a successfully placed BUY order receives
        // an order ID and can be retrieved from the matching engine.
        [Fact]
        public void PlaceBuyOrder_ValidOrder_RegisteredInMatchingEngine()
        {
            // Arrange
            Account account = CreateAccount();

            Order order = CreateOrder(
                account.AccountId, OrderSide.Buy);

            // Act
            Order accepted = _orderService.PlaceBuyOrder(order);

            Order? stored = _matchingEngine.GetOrderById(
                accepted.OrderId);

            // Assert
            Assert.NotNull(stored);
            Assert.NotSame(accepted, stored);
            Assert.Equal(accepted.OrderId, stored.OrderId);
            Assert.Equal(accepted.AccountId, stored.AccountId);
            Assert.Equal(accepted.InstrumentId, stored.InstrumentId);
            Assert.Equal(OrderSide.Buy, stored.Side);
            Assert.Equal(OrderStatus.Pending, stored.Status);
            Assert.Equal(0, stored.FilledQuantity);
            Assert.True(stored.CreatedAt > DateTime.MinValue);
        }

        // TEST 20
        // Verify that separate SELL orders receive different IDs
        // and both remain registered in the matching engine.
        [Fact]
        public void PlaceSellOrder_TwoValidOrders_GetUniqueOrderIds()
        {
            // Arrange
            Account account = CreateAccount();

            _positionService.AddPosition(
                account.AccountId, InstrumentId, 100, 400m);

            Order first = CreateOrder(
                account.AccountId, OrderSide.Sell, 40, 500m);

            Order second = CreateOrder(
                account.AccountId, OrderSide.Sell, 30, 510m);

            // Act
            Order acceptedFirst = _orderService.PlaceSellOrder(first);
            Order acceptedSecond = _orderService.PlaceSellOrder(second);

            // Assert
            Assert.NotEqual(
                acceptedFirst.OrderId,
                acceptedSecond.OrderId);

            Order storedFirst = _matchingEngine.GetOrderById(acceptedFirst.OrderId)!;
            Assert.NotSame(acceptedFirst, storedFirst);
            Assert.Equal(acceptedFirst.OrderId, storedFirst.OrderId);

            Order storedSecond = _matchingEngine.GetOrderById(acceptedSecond.OrderId)!;
            Assert.NotSame(acceptedSecond, storedSecond);
            Assert.Equal(acceptedSecond.OrderId, storedSecond.OrderId);

            var position = _positionService.GetPosition(
                account.AccountId, InstrumentId);

            Assert.NotNull(position);
            Assert.Equal(70, position.ReservedQuantity);
            Assert.Equal(30, position.AvailableQuantity);
        }
    }
}
