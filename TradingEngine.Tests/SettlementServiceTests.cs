
using System;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Services;
using Xunit;

namespace TradingEngine.Tests
{
    public class SettlementServiceTests
    {
        private readonly AccountService _accountService;
        private readonly PositionService _positionService;
        private readonly SettlementService _settlementService;

        private const int InstrumentId = 1;

        public SettlementServiceTests()
        {
            // xUnit creates a new instance of this class for each test.
            // Therefore, every test has independent account and position data.
            _accountService = new AccountService();
            _positionService = new PositionService();

            _settlementService = new SettlementService(
                _accountService, _positionService);
        }

        // Create two accounts and a matching pair of orders.
        // The orders represent an already-matched trade.
        private (Account buyer, Account seller,
                 Order buyOrder, Order sellOrder)
            SetupTrade(
                int quantity = 10,
                decimal buyLimit = 100m,
                decimal sellLimit = 95m)
        {
            Account buyer = _accountService.CreateAccount(
                new Account
                {
                    AccountName = "Buyer",
                    CashBalance = 10000m
                });

            Account seller = _accountService.CreateAccount(
                new Account
                {
                    AccountName = "Seller",
                    CashBalance = 2000m
                });

            // Seller initially owns 20 shares.
            _positionService.AddPosition(
                seller.AccountId, InstrumentId, 20, 80m);

            // Simulate reservations performed by OrderService.
            _accountService.ReserveFunds(
                buyer.AccountId, quantity * buyLimit);

            _positionService.ReserveQuantity(
                seller.AccountId, InstrumentId, quantity);

            Order buyOrder = new Order
            {
                OrderId = 101,
                AccountId = buyer.AccountId,
                InstrumentId = InstrumentId,
                Side = OrderSide.Buy,
                OrderType = OrderType.Limit,
                Quantity = quantity,
                FilledQuantity = quantity,
                LimitPrice = buyLimit,
                Status = OrderStatus.Filled
            };

            Order sellOrder = new Order
            {
                OrderId = 102,
                AccountId = seller.AccountId,
                InstrumentId = InstrumentId,
                Side = OrderSide.Sell,
                OrderType = OrderType.Limit,
                Quantity = quantity,
                FilledQuantity = quantity,
                LimitPrice = sellLimit,
                Status = OrderStatus.Filled
            };

            return (buyer, seller, buyOrder, sellOrder);
        }

        // Construct an execution produced by the matching engine.
        private static Execution CreateExecution(
            int quantity,
            decimal price,
            int executionId = 1)
        {
            return new Execution
            {
                ExecutionId = executionId,
                BuyOrderId = 101,
                SellOrderId = 102,
                ExecutedQuantity = quantity,
                ExecutedPrice = price,
                ExecutedAt = DateTime.UtcNow
            };
        }

        // ===================================================
        // TEST 1: FULL EXECUTION
        // ===================================================

        [Fact]
        public void SettleExecution_FullExecution_UpdatesAccountsAndPositions()
        {
            // Arrange
            // Buyer reserves 10 * 100 = 1000.
            // Seller reserves 10 of their 20 shares.
            var (buyer, seller, buyOrder, sellOrder) = SetupTrade();

            Execution execution = CreateExecution(10, 100m);

            // Act
            _settlementService.SettleExecution(
                execution, buyOrder, sellOrder);

            // Assert: Buyer pays 1000 and receives 10 shares.
            Assert.Equal(9000m, buyer.CashBalance);
            Assert.Equal(9000m, buyer.AvailableCash);
            Assert.Equal(0m, buyer.ReservedCash);

            Position? buyerPosition = _positionService.GetPosition(
                buyer.AccountId, InstrumentId);

            Assert.NotNull(buyerPosition);
            Assert.Equal(10, buyerPosition.Quantity);
            Assert.Equal(0, buyerPosition.ReservedQuantity);
            Assert.Equal(100m, buyerPosition.AverageCost);

            // Seller receives 1000 and delivers 10 shares.
            Assert.Equal(3000m, seller.CashBalance);
            Assert.Equal(3000m, seller.AvailableCash);

            Position? sellerPosition = _positionService.GetPosition(
                seller.AccountId, InstrumentId);

            Assert.NotNull(sellerPosition);
            Assert.Equal(10, sellerPosition.Quantity);
            Assert.Equal(0, sellerPosition.ReservedQuantity);

            // Selling shares does not change remaining average cost.
            Assert.Equal(80m, sellerPosition.AverageCost);
        }

        // ===================================================
        // TEST 2: PARTIAL EXECUTION
        // ===================================================

        [Fact]
        public void SettleExecution_PartialExecution_PreservesReservations()
        {
            // Arrange
            // Both orders are for 10 shares, but only 4 match.
            var (buyer, seller, buyOrder, sellOrder) = SetupTrade();

            buyOrder.FilledQuantity = 4;
            sellOrder.FilledQuantity = 4;
            buyOrder.Status = OrderStatus.PartiallyFilled;
            sellOrder.Status = OrderStatus.PartiallyFilled;

            Execution execution = CreateExecution(4, 100m);

            // Act
            _settlementService.SettleExecution(
                execution, buyOrder, sellOrder);

            // Assert: Only 400 of the reserved 1000 is consumed.
            Assert.Equal(9600m, buyer.CashBalance);
            Assert.Equal(9000m, buyer.AvailableCash);
            Assert.Equal(600m, buyer.ReservedCash);

            // Seller receives 400.
            Assert.Equal(2400m, seller.CashBalance);

            Position? buyerPosition = _positionService.GetPosition(
                buyer.AccountId, InstrumentId);

            Assert.NotNull(buyerPosition);
            Assert.Equal(4, buyerPosition.Quantity);

            Position? sellerPosition = _positionService.GetPosition(
                seller.AccountId, InstrumentId);

            Assert.NotNull(sellerPosition);

            // Initially 20 shares, 10 reserved.
            // 4 delivered; 6 remain reserved.
            Assert.Equal(16, sellerPosition.Quantity);
            Assert.Equal(6, sellerPosition.ReservedQuantity);
            Assert.Equal(10, sellerPosition.AvailableQuantity);
        }

        // ===================================================
        // TEST 3: PRICE IMPROVEMENT
        // ===================================================

        [Fact]
        public void SettleExecution_BetterPrice_ReleasesUnusedCash()
        {
            // Arrange
            // Buyer reserves 1000 at a limit price of 100.
            // Trade executes at 95, costing only 950.
            var (buyer, seller, buyOrder, sellOrder) = SetupTrade();

            Execution execution = CreateExecution(10, 95m);

            // Act
            _settlementService.SettleExecution(
                execution, buyOrder, sellOrder);

            // Assert: 950 deducted; unused 50 becomes available.
            Assert.Equal(9050m, buyer.CashBalance);
            Assert.Equal(9050m, buyer.AvailableCash);
            Assert.Equal(0m, buyer.ReservedCash);

            // Seller receives actual execution amount, not limit amount.
            Assert.Equal(2950m, seller.CashBalance);

            Position? buyerPosition = _positionService.GetPosition(
                buyer.AccountId, InstrumentId);

            Assert.NotNull(buyerPosition);
            Assert.Equal(95m, buyerPosition.AverageCost);
        }

        // ===================================================
        // TEST 4: MULTIPLE EXECUTIONS FOR THE SAME ORDERS
        // ===================================================

        [Fact]
        public void SettleExecution_MultipleExecutions_SettlesCumulatively()
        {
            // Arrange
            // Order quantity = 10 shares.
            // First execution = 4 shares @ 100.
            // Second execution = 6 shares @ 95.
            var (buyer, seller, buyOrder, sellOrder) = SetupTrade();

            Execution first = CreateExecution(4, 100m, 1);
            Execution second = CreateExecution(6, 95m, 2);

            // Act
            _settlementService.SettleExecution(
                first, buyOrder, sellOrder);

            _settlementService.SettleExecution(
                second, buyOrder, sellOrder);

            // Assert: Total cost = 400 + 570 = 970.
            Assert.Equal(9030m, buyer.CashBalance);
            Assert.Equal(9030m, buyer.AvailableCash);
            Assert.Equal(0m, buyer.ReservedCash);

            // Seller receives total proceeds of 970.
            Assert.Equal(2970m, seller.CashBalance);

            Position? buyerPosition = _positionService.GetPosition(
                buyer.AccountId, InstrumentId);

            Assert.NotNull(buyerPosition);
            Assert.Equal(10, buyerPosition.Quantity);

            // Weighted average purchase cost:
            // (4 * 100 + 6 * 95) / 10 = 97.
            Assert.Equal(97m, buyerPosition.AverageCost);

            Position? sellerPosition = _positionService.GetPosition(
                seller.AccountId, InstrumentId);

            Assert.NotNull(sellerPosition);
            Assert.Equal(10, sellerPosition.Quantity);
            Assert.Equal(0, sellerPosition.ReservedQuantity);
        }

        // ===================================================
        // TEST 5: DUPLICATE EXECUTION
        // ===================================================

        [Fact]
        public void SettleExecution_DuplicateExecution_ThrowsWithoutFurtherChanges()
        {
            // Arrange
            var (buyer, seller, buyOrder, sellOrder) = SetupTrade();

            Execution execution = CreateExecution(10, 100m);

            // First settlement succeeds.
            _settlementService.SettleExecution(
                execution, buyOrder, sellOrder);

            // Save balances after successful settlement.
            decimal buyerBalance = buyer.CashBalance;
            decimal sellerBalance = seller.CashBalance;

            // Act: Attempt to settle the same execution again.
            Assert.Throws<InvalidOperationException>(() =>
                _settlementService.SettleExecution(
                    execution, buyOrder, sellOrder));

            // Assert: No additional money or shares transferred.
            Assert.Equal(buyerBalance, buyer.CashBalance);
            Assert.Equal(sellerBalance, seller.CashBalance);

            Position? buyerPosition = _positionService.GetPosition(
                buyer.AccountId, InstrumentId);

            Assert.NotNull(buyerPosition);
            Assert.Equal(10, buyerPosition.Quantity);

            Position? sellerPosition = _positionService.GetPosition(
                seller.AccountId, InstrumentId);

            Assert.NotNull(sellerPosition);
            Assert.Equal(10, sellerPosition.Quantity);
        }

        // ===================================================
        // TEST 6: INVALID EXECUTION QUANTITY
        // ===================================================

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void SettleExecution_InvalidQuantity_Throws(int quantity)
        {
            // Arrange
            var (buyer, seller, buyOrder, sellOrder) = SetupTrade();

            Execution execution = CreateExecution(quantity, 100m);

            // Capture original account balances before attempting settlement.
            decimal buyerCash = buyer.CashBalance;
            decimal sellerCash = seller.CashBalance;

            // Act
            Assert.Throws<ArgumentException>(() =>
                _settlementService.SettleExecution(
                    execution, buyOrder, sellOrder));

            // Assert: Invalid executions must not modify balances.
            Assert.Equal(buyerCash, buyer.CashBalance);
            Assert.Equal(sellerCash, seller.CashBalance);
            Assert.Equal(1000m, buyer.ReservedCash);
        }

        // ===================================================
        // TEST 7: INVALID ORDER IDENTIFIERS
        // ===================================================

        [Fact]
        public void SettleExecution_MismatchedOrderId_Throws()
        {
            // Arrange
            var (buyer, seller, buyOrder, sellOrder) = SetupTrade();

            Execution execution = CreateExecution(10, 100m);

            // Execution refers to a different buyer order.
            execution.BuyOrderId = 999;

            // Act
            Assert.Throws<ArgumentException>(() =>
                _settlementService.SettleExecution(
                    execution, buyOrder, sellOrder));

            // Assert: No money transferred.
            Assert.Equal(10000m, buyer.CashBalance);
            Assert.Equal(2000m, seller.CashBalance);
        }

        // ===================================================
        // TEST 8: INVALID EXECUTION PRICE
        // ===================================================

        [Theory]
        [InlineData(0)]
        [InlineData(-10)]
        public void SettleExecution_InvalidPrice_Throws(decimal price)
        {
            // Arrange
            var (buyer, seller, buyOrder, sellOrder) = SetupTrade();

            Execution execution = CreateExecution(10, price);

            // Act
            Assert.Throws<ArgumentException>(() =>
                _settlementService.SettleExecution(
                    execution, buyOrder, sellOrder));

            // Assert: Reservations and balances remain unchanged.
            Assert.Equal(10000m, buyer.CashBalance);
            Assert.Equal(1000m, buyer.ReservedCash);
            Assert.Equal(2000m, seller.CashBalance);
        }

        // ===================================================
        // TEST 9: FAILURE AT SELLER POSITION SETTLEMENT
        // ===================================================

        // This test demonstrates a KNOWN issue in the current design.
        // It is skipped until atomic settlement is implemented.
        //
        // Buyer cash and seller cash can change successfully, while
        // the final seller position update fails.
        //
        // The desired behavior is to leave ALL balances unchanged.

        [Fact(Skip = "Requires atomic settlement implementation")]
        public void SettleExecution_PositionFailure_DoesNotPartiallySettle()
        {
            // Arrange
            var (buyer, seller, buyOrder, sellOrder) = SetupTrade();

            // Remove the seller's reservation before settlement.
            // This causes SettleSellPosition to throw.
            _positionService.ReleaseReservedQuantity(
                seller.AccountId, InstrumentId, 10);

            Execution execution = CreateExecution(10, 100m);

            // Act: Settlement should fail.
            Assert.Throws<InvalidOperationException>(() =>
                _settlementService.SettleExecution(
                    execution, buyOrder, sellOrder));

            // Desired assertions:
            // Failed settlement must not transfer money.
            Assert.Equal(10000m, buyer.CashBalance);
            Assert.Equal(2000m, seller.CashBalance);

            // Buyer must not receive shares.
            Assert.Null(_positionService.GetPosition(
                buyer.AccountId, InstrumentId));
        }
    }
}
