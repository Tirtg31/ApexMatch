
using System;
using TradingEngine.Core.Services;
using Xunit;

namespace TradingEngine.Tests
{
    public class PositionReservationTests
    {
        private readonly PositionService _positionService = new();

        private const int AccountId = 101;
        private const int InstrumentId = 1;

        private void CreatePosition(
            int quantity = 100,
            decimal price = 450m)
        {
            _positionService.AddPosition(
                AccountId,
                InstrumentId,
                quantity,
                price);
        }

        [Fact]
        public void ReserveQuantity_ValidQuantity_UpdatesReservedAndAvailable()
        {
            // Arrange
            CreatePosition();

            // Act
            _positionService.ReserveQuantity(AccountId, InstrumentId, 60);

            // Assert
            var position = _positionService.GetPosition(AccountId, InstrumentId);

            Assert.NotNull(position);
            Assert.Equal(100, position.Quantity);
            Assert.Equal(60, position.ReservedQuantity);
            Assert.Equal(40, position.AvailableQuantity);
            Assert.Equal(450m, position.AverageCost);
        }

        [Fact]
        public void ReserveQuantity_InsufficientShares_ThrowsException()
        {
            // Arrange
            CreatePosition();

            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
                _positionService.ReserveQuantity(AccountId, InstrumentId, 120));

            var position = _positionService.GetPosition(AccountId, InstrumentId);

            Assert.NotNull(position);
            Assert.Equal(0, position.ReservedQuantity);
            Assert.Equal(100, position.AvailableQuantity);
        }

        [Fact]
        public void ReserveQuantity_MultipleReservations_AccumulatesReservedShares()
        {
            // Arrange
            CreatePosition();

            // Act
            _positionService.ReserveQuantity(AccountId, InstrumentId, 40);
            _positionService.ReserveQuantity(AccountId, InstrumentId, 30);
            _positionService.ReserveQuantity(AccountId, InstrumentId, 20);

            // Assert
            var position = _positionService.GetPosition(AccountId, InstrumentId);

            Assert.NotNull(position);
            Assert.Equal(100, position.Quantity);
            Assert.Equal(90, position.ReservedQuantity);
            Assert.Equal(10, position.AvailableQuantity);
        }

        [Fact]
        public void ReserveQuantity_ZeroQuantity_ThrowsException()
        {
            CreatePosition();

            Assert.Throws<ArgumentException>(() =>
                _positionService.ReserveQuantity(AccountId, InstrumentId, 0));
        }

        [Fact]
        public void ReleaseReservedQuantity_ValidRelease_UpdatesAvailability()
        {
            // Arrange
            CreatePosition();
            _positionService.ReserveQuantity(AccountId, InstrumentId, 60);

            // Act
            _positionService.ReleaseReservedQuantity(AccountId, InstrumentId, 25);

            // Assert
            var position = _positionService.GetPosition(AccountId, InstrumentId);

            Assert.NotNull(position);
            Assert.Equal(100, position.Quantity);
            Assert.Equal(35, position.ReservedQuantity);
            Assert.Equal(65, position.AvailableQuantity);
        }

        [Fact]
        public void ReleaseReservedQuantity_ExceedsReserved_ThrowsException()
        {
            // Arrange
            CreatePosition();
            _positionService.ReserveQuantity(AccountId, InstrumentId, 30);

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() =>
                _positionService.ReleaseReservedQuantity(AccountId, InstrumentId, 50));

            var position = _positionService.GetPosition(AccountId, InstrumentId);

            Assert.NotNull(position);
            Assert.Equal(30, position.ReservedQuantity);
            Assert.Equal(70, position.AvailableQuantity);
        }

        [Fact]
        public void ReleaseReservedQuantity_FullRelease_RestoresAvailability()
        {
            // Arrange
            CreatePosition();
            _positionService.ReserveQuantity(AccountId, InstrumentId, 80);

            // Act
            _positionService.ReleaseReservedQuantity(AccountId, InstrumentId, 80);

            // Assert
            var position = _positionService.GetPosition(AccountId, InstrumentId);

            Assert.NotNull(position);
            Assert.Equal(100, position.Quantity);
            Assert.Equal(0, position.ReservedQuantity);
            Assert.Equal(100, position.AvailableQuantity);
        }

        [Fact]
        public void SettleSellPosition_PartialExecution_ReducesTotalAndReserved()
        {
            // Arrange
            CreatePosition();
            _positionService.ReserveQuantity(AccountId, InstrumentId, 60);

            // Act
            _positionService.SettleSellPosition(AccountId, InstrumentId, 25);

            // Assert
            var position = _positionService.GetPosition(AccountId, InstrumentId);

            Assert.NotNull(position);
            Assert.Equal(75, position.Quantity);
            Assert.Equal(35, position.ReservedQuantity);
            Assert.Equal(40, position.AvailableQuantity);
            Assert.Equal(450m, position.AverageCost);
        }

        [Fact]
        public void SettleSellPosition_ExceedsReserved_ThrowsException()
        {
            // Arrange
            CreatePosition();
            _positionService.ReserveQuantity(AccountId, InstrumentId, 20);

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() =>
                _positionService.SettleSellPosition(AccountId, InstrumentId, 50));

            var position = _positionService.GetPosition(AccountId, InstrumentId);

            Assert.NotNull(position);
            Assert.Equal(100, position.Quantity);
            Assert.Equal(20, position.ReservedQuantity);
            Assert.Equal(80, position.AvailableQuantity);
        }

        [Fact]
        public void SettleSellPosition_FullLiquidation_RemovesPosition()
        {
            // Arrange
            CreatePosition(50);
            _positionService.ReserveQuantity(AccountId, InstrumentId, 50);

            // Act
            _positionService.SettleSellPosition(AccountId, InstrumentId, 50);

            // Assert
            Assert.Null(_positionService.GetPosition(AccountId, InstrumentId));
            Assert.Empty(_positionService.GetPositionsByAccount(AccountId));
        }

        [Fact]
        public void SettleSellPosition_MultiplePartialExecutions_CorrectlyUpdatesPosition()
        {
            // Arrange
            CreatePosition();
            _positionService.ReserveQuantity(AccountId, InstrumentId, 80);

            // Act
            _positionService.SettleSellPosition(AccountId, InstrumentId, 20);
            _positionService.SettleSellPosition(AccountId, InstrumentId, 30);

            // Assert
            var position = _positionService.GetPosition(AccountId, InstrumentId);

            Assert.NotNull(position);
            Assert.Equal(50, position.Quantity);
            Assert.Equal(30, position.ReservedQuantity);
            Assert.Equal(20, position.AvailableQuantity);
        }

        [Fact]
        public void PartialFill_ThenCancellation_ReleasesOnlyUnfilledShares()
        {
            // Arrange
            CreatePosition();
            _positionService.ReserveQuantity(AccountId, InstrumentId, 60);

            // Act: 25 shares execute
            _positionService.SettleSellPosition(AccountId, InstrumentId, 25);

            // Remaining 35 shares are cancelled
            _positionService.ReleaseReservedQuantity(AccountId, InstrumentId, 35);

            // Assert
            var position = _positionService.GetPosition(AccountId, InstrumentId);

            Assert.NotNull(position);
            Assert.Equal(75, position.Quantity);
            Assert.Equal(0, position.ReservedQuantity);
            Assert.Equal(75, position.AvailableQuantity);
            Assert.Equal(450m, position.AverageCost);
        }

        [Fact]
        public void AddPosition_WhileSharesReserved_PreservesReservation()
        {
            // Arrange
            CreatePosition(100, 400m);
            _positionService.ReserveQuantity(AccountId, InstrumentId, 60);

            // Act: Buy 20 additional shares at 500
            _positionService.AddPosition(AccountId, InstrumentId, 20, 500m);

            // Assert
            var position = _positionService.GetPosition(AccountId, InstrumentId);

            Assert.NotNull(position);
            Assert.Equal(120, position.Quantity);
            Assert.Equal(60, position.ReservedQuantity);
            Assert.Equal(60, position.AvailableQuantity);

            // (100 * 400 + 20 * 500) / 120
            Assert.Equal(416.66666666666666666666666667m,
                         position.AverageCost);
        }

        [Fact]
        public void ReducePosition_CannotReduceReservedShares()
        {
            // Arrange
            CreatePosition();
            _positionService.ReserveQuantity(AccountId, InstrumentId, 80);

            // Act & Assert: Only 20 shares are available
            Assert.Throws<ArgumentException>(() =>
                _positionService.ReducePosition(AccountId, InstrumentId, 30));

            var position = _positionService.GetPosition(AccountId, InstrumentId);

            Assert.NotNull(position);
            Assert.Equal(100, position.Quantity);
            Assert.Equal(80, position.ReservedQuantity);
            Assert.Equal(20, position.AvailableQuantity);
        }

        [Fact]
        public void ReservationLifecycle_MultipleOrders_CorrectFinalState()
        {
            // Arrange
            CreatePosition();

            // Three sell orders: 40, 30, 20
            _positionService.ReserveQuantity(AccountId, InstrumentId, 40);
            _positionService.ReserveQuantity(AccountId, InstrumentId, 30);
            _positionService.ReserveQuantity(AccountId, InstrumentId, 20);

            // Act: Cancel first order (40)
            _positionService.ReleaseReservedQuantity(AccountId, InstrumentId, 40);

            // Second order executes 20
            _positionService.SettleSellPosition(AccountId, InstrumentId, 20);

            // Cancel remaining 10 from second order
            _positionService.ReleaseReservedQuantity(AccountId, InstrumentId, 10);

            // Third order remains open for 20
            var position = _positionService.GetPosition(AccountId, InstrumentId);

            // Assert
            Assert.NotNull(position);
            Assert.Equal(80, position.Quantity);
            Assert.Equal(20, position.ReservedQuantity);
            Assert.Equal(60, position.AvailableQuantity);
            Assert.Equal(450m, position.AverageCost);
        }
    }
}
