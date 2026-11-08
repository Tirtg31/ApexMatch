using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Services;
using Xunit;

namespace TradingEngine.Tests
{
    /// <summary>
    /// Tests the OrderService.CancelBuyOrder(int) and CancelSellOrder(int) APIs.
    /// Each test gets independent in-memory services. Partial-fill tests deliberately
    /// settle executions before cancellation, matching the current design contract.
    /// </summary>
    public class OrderCancellationTests
    {
        private const int InstrumentId = 1;
        private readonly AccountService accounts = new();
        private readonly PositionService positions = new();
        private readonly OrderMatchingEngine engine = new();
        private readonly OrderService orders;
        private readonly SettlementService settlements;

        public OrderCancellationTests()
        {
            orders = new OrderService(accounts, engine, positions);
            settlements = new SettlementService(accounts, positions);
        }

        private Account Account(decimal cash = 10_000m) => accounts.CreateAccount(new Account
        {
            AccountName = "Trader",
            CashBalance = cash
        });

        private static Order Request(int accountId, OrderSide side, int quantity = 100, decimal price = 100m) => new()
        {
            AccountId = accountId,
            InstrumentId = InstrumentId,
            Side = side,
            OrderType = OrderType.Limit,
            Quantity = quantity,
            LimitPrice = price
        };

        private Account Snapshot(int accountId) => accounts.GetAccountById(accountId)!;
        private Position Position(int accountId) => positions.GetPosition(accountId, InstrumentId)!;
        private Order Stored(int orderId) => engine.GetOrderById(orderId)!;

        private void MatchAndSettle()
        {
            var executions = engine.LimitOrderMatcher(InstrumentId);
            Assert.NotEmpty(executions);
            foreach (var execution in executions)
            {
                settlements.SettleExecution(execution,
                    Stored(execution.BuyOrderId), Stored(execution.SellOrderId));
            }
        }

        // 01: Pending buy cancellation releases the full unused cash reservation.
        [Fact]
        public void CancelPendingBuy_ReleasesAllCash()
        {
            var buyer = Account();
            var buy = orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, 50, 100m));
            Assert.Equal(5_000m, Snapshot(buyer.AccountId).ReservedCash);

            Assert.True(orders.CancelBuyOrder(buy.OrderId));
            Assert.Equal(OrderStatus.Cancelled, Stored(buy.OrderId).Status);
            Assert.Equal(0m, Snapshot(buyer.AccountId).ReservedCash);
            Assert.Equal(10_000m, Snapshot(buyer.AccountId).AvailableCash);
            Assert.Equal(10_000m, Snapshot(buyer.AccountId).CashBalance);
        }

        // 02: Pending sell cancellation restores availability without removing owned shares.
        [Fact]
        public void CancelPendingSell_ReleasesAllReservedShares()
        {
            var seller = Account();
            positions.AddPosition(seller.AccountId, InstrumentId, 100, 80m);
            var sell = orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 75, 100m));

            Assert.True(orders.CancelSellOrder(sell.OrderId));
            Assert.Equal(OrderStatus.Cancelled, Stored(sell.OrderId).Status);
            Assert.Equal(100, Position(seller.AccountId).Quantity);
            Assert.Equal(0, Position(seller.AccountId).ReservedQuantity);
            Assert.Equal(100, Position(seller.AccountId).AvailableQuantity);
        }

        // 03: A partly filled buy releases only its unfilled portion at the limit price.
        [Fact]
        public void CancelPartiallyFilledBuy_ReleasesOnlyRemainingCash()
        {
            var buyer = Account(20_000m);
            var seller = Account();
            positions.AddPosition(seller.AccountId, InstrumentId, 40, 70m);
            var buy = orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, 100, 100m));
            orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 40, 90m));
            MatchAndSettle();

            Assert.Equal(40, Stored(buy.OrderId).FilledQuantity);
            Assert.Equal(6_000m, Snapshot(buyer.AccountId).ReservedCash);
            Assert.True(orders.CancelBuyOrder(buy.OrderId));
            Assert.Equal(OrderStatus.Cancelled, Stored(buy.OrderId).Status);
            Assert.Equal(40, Stored(buy.OrderId).FilledQuantity);
            Assert.Equal(0m, Snapshot(buyer.AccountId).ReservedCash);
            Assert.Equal(16_000m, Snapshot(buyer.AccountId).AvailableCash);
            Assert.Equal(40, Position(buyer.AccountId).Quantity);
        }

        // 04: A partly filled sell releases only the outstanding share reservation.
        [Fact]
        public void CancelPartiallyFilledSell_ReleasesOnlyRemainingShares()
        {
            var seller = Account();
            var buyer = Account(20_000m);
            positions.AddPosition(seller.AccountId, InstrumentId, 100, 70m);
            var sell = orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 100, 90m));
            orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, 40, 100m));
            MatchAndSettle();

            Assert.Equal(40, Stored(sell.OrderId).FilledQuantity);
            Assert.Equal(60, Position(seller.AccountId).ReservedQuantity);
            Assert.True(orders.CancelSellOrder(sell.OrderId));
            Assert.Equal(OrderStatus.Cancelled, Stored(sell.OrderId).Status);
            Assert.Equal(60, Position(seller.AccountId).Quantity);
            Assert.Equal(0, Position(seller.AccountId).ReservedQuantity);
            Assert.Equal(60, Position(seller.AccountId).AvailableQuantity);
        }

        // 05: Repeated buy cancellation does not release the same cash twice.
        [Fact]
        public void CancelBuyTwice_ReleasesOnce()
        {
            var buyer = Account();
            var buy = orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy));
            Assert.True(orders.CancelBuyOrder(buy.OrderId));
            Assert.False(orders.CancelBuyOrder(buy.OrderId));
            Assert.Equal(0m, Snapshot(buyer.AccountId).ReservedCash);
            Assert.Equal(10_000m, Snapshot(buyer.AccountId).AvailableCash);
        }

        // 06: Repeated sell cancellation does not release the same shares twice.
        [Fact]
        public void CancelSellTwice_ReleasesOnce()
        {
            var seller = Account();
            positions.AddPosition(seller.AccountId, InstrumentId, 100, 70m);
            var sell = orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell));
            Assert.True(orders.CancelSellOrder(sell.OrderId));
            Assert.False(orders.CancelSellOrder(sell.OrderId));
            Assert.Equal(0, Position(seller.AccountId).ReservedQuantity);
        }

        // 07: A fully filled buy cannot be cancelled or refunded.
        [Fact]
        public void CancelFilledBuy_ReturnsFalseWithoutRefund()
        {
            var buyer = Account();
            var seller = Account();
            positions.AddPosition(seller.AccountId, InstrumentId, 50, 70m);
            var buy = orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, 50, 100m));
            orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 50, 90m));
            MatchAndSettle();

            Assert.False(orders.CancelBuyOrder(buy.OrderId));
            Assert.Equal(OrderStatus.Filled, Stored(buy.OrderId).Status);
            Assert.Equal(5_000m, Snapshot(buyer.AccountId).CashBalance);
            Assert.Equal(0m, Snapshot(buyer.AccountId).ReservedCash);
        }

        // 08: A fully filled sell cannot be cancelled or have shares restored.
        [Fact]
        public void CancelFilledSell_ReturnsFalseWithoutRestoringShares()
        {
            var buyer = Account();
            var seller = Account();
            positions.AddPosition(seller.AccountId, InstrumentId, 50, 70m);
            orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, 50, 100m));
            var sell = orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 50, 90m));
            MatchAndSettle();

            Assert.False(orders.CancelSellOrder(sell.OrderId));
            Assert.Equal(OrderStatus.Filled, Stored(sell.OrderId).Status);
            Assert.Null(positions.GetPosition(seller.AccountId, InstrumentId));
        }

        // 09-10: Cancellation using the wrong side is rejected without changing state.
        [Fact]
        public void CancelBuy_WithSellOrderId_ThrowsWithoutMutation()
        {
            var seller = Account();
            positions.AddPosition(seller.AccountId, InstrumentId, 100, 70m);
            var sell = orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell));
            Assert.Throws<InvalidOperationException>(() => orders.CancelBuyOrder(sell.OrderId));
            Assert.Equal(OrderStatus.Pending, Stored(sell.OrderId).Status);
            Assert.Equal(100, Position(seller.AccountId).ReservedQuantity);
        }

        [Fact]
        public void CancelSell_WithBuyOrderId_ThrowsWithoutMutation()
        {
            var buyer = Account();
            var buy = orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy));
            Assert.Throws<InvalidOperationException>(() => orders.CancelSellOrder(buy.OrderId));
            Assert.Equal(OrderStatus.Pending, Stored(buy.OrderId).Status);
            Assert.Equal(10_000m, Snapshot(buyer.AccountId).ReservedCash);
        }

        // 11-14: Invalid and unknown IDs are rejected for both directions.
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(999999)]
        public void CancelBuy_UnknownOrder_Throws(int orderId)
        {
            Assert.Throws<ArgumentException>(() => orders.CancelBuyOrder(orderId));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(999999)]
        public void CancelSell_UnknownOrder_Throws(int orderId)
        {
            Assert.Throws<ArgumentException>(() => orders.CancelSellOrder(orderId));
        }

        // 15: The caller's old snapshot is NOT used to compute the refund.
        [Fact]
        public void CancelBuy_StaleOrTamperedSnapshot_DoesNotChangeRefund()
        {
            var buyer = Account();
            var returned = orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, 50, 100m));
            returned.Quantity = 999;
            returned.LimitPrice = 1m;
            returned.FilledQuantity = 49;
            returned.AccountId = 999;

            Assert.True(orders.CancelBuyOrder(returned.OrderId));
            Assert.Equal(0m, Snapshot(buyer.AccountId).ReservedCash);
            Assert.Equal(10_000m, Snapshot(buyer.AccountId).AvailableCash);
        }

        // 16: The caller's old snapshot is NOT used to compute share releases.
        [Fact]
        public void CancelSell_StaleOrTamperedSnapshot_DoesNotChangeShareRelease()
        {
            var seller = Account();
            positions.AddPosition(seller.AccountId, InstrumentId, 100, 70m);
            var returned = orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 50, 100m));
            returned.Quantity = 999;
            returned.FilledQuantity = 49;
            returned.AccountId = 999;
            returned.InstrumentId = 999;

            Assert.True(orders.CancelSellOrder(returned.OrderId));
            Assert.Equal(0, Position(seller.AccountId).ReservedQuantity);
            Assert.Equal(100, Position(seller.AccountId).AvailableQuantity);
        }

        // 17: Cancelling one buy leaves the other buy's reservation untouched.
        [Fact]
        public void CancelOneOfTwoBuys_PreservesOtherReservation()
        {
            var buyer = Account(20_000m);
            var first = orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, 50, 100m));
            var second = orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, 75, 100m));
            Assert.True(orders.CancelBuyOrder(first.OrderId));
            Assert.Equal(7_500m, Snapshot(buyer.AccountId).ReservedCash);
            Assert.Equal(OrderStatus.Pending, Stored(second.OrderId).Status);
        }

        // 18: Cancelling one sell leaves the other sell's reservation untouched.
        [Fact]
        public void CancelOneOfTwoSells_PreservesOtherReservation()
        {
            var seller = Account();
            positions.AddPosition(seller.AccountId, InstrumentId, 100, 70m);
            var first = orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 30, 100m));
            var second = orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 40, 100m));
            Assert.True(orders.CancelSellOrder(first.OrderId));
            Assert.Equal(40, Position(seller.AccountId).ReservedQuantity);
            Assert.Equal(OrderStatus.Pending, Stored(second.OrderId).Status);
        }

        // 19: Cancelled buy is skipped by matching, not executed later.
        [Fact]
        public void CancelledBuy_IsNotMatched()
        {
            var buyer = Account();
            var seller = Account();
            positions.AddPosition(seller.AccountId, InstrumentId, 20, 70m);
            var buy = orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, 20, 100m));
            Assert.True(orders.CancelBuyOrder(buy.OrderId));
            orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 20, 90m));
            Assert.Empty(engine.LimitOrderMatcher(InstrumentId));
            Assert.Equal(OrderStatus.Cancelled, Stored(buy.OrderId).Status);
        }

        // 20: Cancelled sell is skipped by matching, not executed later.
        [Fact]
        public void CancelledSell_IsNotMatched()
        {
            var buyer = Account();
            var seller = Account();
            positions.AddPosition(seller.AccountId, InstrumentId, 20, 70m);
            var sell = orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 20, 90m));
            Assert.True(orders.CancelSellOrder(sell.OrderId));
            orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, 20, 100m));
            Assert.Empty(engine.LimitOrderMatcher(InstrumentId));
            Assert.Equal(OrderStatus.Cancelled, Stored(sell.OrderId).Status);
        }

        // 21-22: Simultaneous repeated cancellation should release once. These
        // are useful stress checks but do NOT prove cancellation/matching atomicity.
        [Fact]
        public async Task ConcurrentDuplicateBuyCancellation_ReleasesOnce()
        {
            var buyer = Account();
            var buy = orders.PlaceBuyOrder(Request(buyer.AccountId, OrderSide.Buy, 50, 100m));
            int successes = 0;
            var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            {
                if (orders.CancelBuyOrder(buy.OrderId)) Interlocked.Increment(ref successes);
            })).ToArray();
            await Task.WhenAll(tasks);
            Assert.Equal(1, successes);
            Assert.Equal(0m, Snapshot(buyer.AccountId).ReservedCash);
        }

        [Fact]
        public async Task ConcurrentDuplicateSellCancellation_ReleasesOnce()
        {
            var seller = Account();
            positions.AddPosition(seller.AccountId, InstrumentId, 100, 70m);
            var sell = orders.PlaceSellOrder(Request(seller.AccountId, OrderSide.Sell, 50, 100m));
            int successes = 0;
            var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            {
                if (orders.CancelSellOrder(sell.OrderId)) Interlocked.Increment(ref successes);
            })).ToArray();
            await Task.WhenAll(tasks);
            Assert.Equal(1, successes);
            Assert.Equal(0, Position(seller.AccountId).ReservedQuantity);
        }
    }
}
