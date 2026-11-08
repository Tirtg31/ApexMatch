
using System;
using System.Linq;
using TradingEngine.Core.Services;
using Xunit;

namespace TradingEngine.Tests
{
    public class PositionServiceTests
    {
        private readonly PositionService _positionService;

        public PositionServiceTests()
        {
            _positionService = new PositionService();
        }

        // TEST 1: Adding a position for the first time
        [Fact]
        public void AddPosition_NewInstrument_CreatesPosition()
        {
            _positionService.AddPosition(1, 101, 10, 100m);

            var position = _positionService.GetPosition(1, 101);

            Assert.NotNull(position);
            Assert.Equal(1, position.AccountId);
            Assert.Equal(101, position.InstrumentId);
            Assert.Equal(10, position.Quantity);
            Assert.Equal(100m, position.AverageCost);
        }

        // TEST 2: Buying additional shares updates weighted average cost
        [Fact]
        public void AddPosition_ExistingInstrument_UpdatesAverageCost()
        {
            _positionService.AddPosition(1, 101, 10, 100m);
            _positionService.AddPosition(1, 101, 10, 120m);

            var position = _positionService.GetPosition(1, 101);

            Assert.NotNull(position);
            Assert.Equal(20, position.Quantity);
            Assert.Equal(110m, position.AverageCost);
        }

        // TEST 3: Different accounts have independent positions
        [Fact]
        public void AddPosition_DifferentAccounts_MaintainsSeparatePositions()
        {
            _positionService.AddPosition(1, 101, 10, 100m);
            _positionService.AddPosition(2, 101, 20, 150m);

            var position1 = _positionService.GetPosition(1, 101);
            var position2 = _positionService.GetPosition(2, 101);

            Assert.NotNull(position1);
            Assert.NotNull(position2);
            Assert.Equal(10, position1.Quantity);
            Assert.Equal(20, position2.Quantity);
            Assert.Equal(100m, position1.AverageCost);
            Assert.Equal(150m, position2.AverageCost);
            Assert.NotEqual(position1.PositionId, position2.PositionId);
        }

        // TEST 4: One account can hold multiple instruments
        [Fact]
        public void GetPositionsByAccount_ReturnsOnlyAccountPositions()
        {
            _positionService.AddPosition(1, 101, 10, 100m);
            _positionService.AddPosition(1, 102, 5, 200m);
            _positionService.AddPosition(2, 103, 8, 300m);

            var positions = _positionService.GetPositionsByAccount(1);

            Assert.Equal(2, positions.Count);
            Assert.True(positions.ContainsKey(101));
            Assert.True(positions.ContainsKey(102));
            Assert.False(positions.ContainsKey(103));
        }

        // TEST 5: Reducing holdings does not change average cost
        [Fact]
        public void ReducePosition_PartialSale_KeepsAverageCostUnchanged()
        {
            _positionService.AddPosition(1, 101, 10, 100m);
            _positionService.AddPosition(1, 101, 10, 120m);

            _positionService.ReducePosition(1, 101, 5);

            var position = _positionService.GetPosition(1, 101);

            Assert.NotNull(position);
            Assert.Equal(15, position.Quantity);
            Assert.Equal(110m, position.AverageCost);
        }

        // TEST 6: Selling all shares removes the position
        [Fact]
        public void ReducePosition_FullSale_RemovesPosition()
        {
            _positionService.AddPosition(1, 101, 10, 100m);

            _positionService.ReducePosition(1, 101, 10);

            Assert.Null(_positionService.GetPosition(1, 101));
        }

        // TEST 7: Selling more shares than owned is rejected
        [Fact]
        public void ReducePosition_InsufficientShares_ThrowsException()
        {
            _positionService.AddPosition(1, 101, 10, 100m);

            Assert.Throws<ArgumentException>(() =>
                _positionService.ReducePosition(1, 101, 15));

            Assert.Equal(10, _positionService.GetPosition(1, 101)!.Quantity);
        }

        // TEST 8: Cannot reduce a nonexistent position
        [Fact]
        public void ReducePosition_PositionDoesNotExist_ThrowsException()
        {
            Assert.Throws<ArgumentException>(() =>
                _positionService.ReducePosition(1, 101, 5));
        }

        // TEST 9: Available quantity equals owned quantity (before reservations)
        [Fact]
        public void GetAvailableQuantity_ExistingPosition_ReturnsQuantity()
        {
            _positionService.AddPosition(1, 101, 25, 100m);

            int available = _positionService.GetAvailableQuantity(1, 101);

            Assert.Equal(25, available);
        }

        // TEST 10: Unknown position returns null
        [Fact]
        public void GetPosition_UnknownInstrument_ReturnsNull()
        {
            var position = _positionService.GetPosition(1, 999);

            Assert.Null(position);
        }

        // TEST 11: Adding positions with invalid inputs is rejected
        [Theory]
        [InlineData(0, 101, 10, 100)]
        [InlineData(1, 0, 10, 100)]
        [InlineData(1, 101, 0, 100)]
        [InlineData(1, 101, -5, 100)]
        [InlineData(1, 101, 10, 0)]
        [InlineData(1, 101, 10, -50)]
        public void AddPosition_InvalidInputs_ThrowsException(
            int accountId, int instrumentId, int quantity, int price)
        {
            Assert.Throws<ArgumentException>(() =>
                _positionService.AddPosition(
                    accountId, instrumentId, quantity, price));
        }

        // TEST 12: Reducing zero or negative quantity is rejected
        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void ReducePosition_InvalidQuantity_ThrowsException(int quantity)
        {
            _positionService.AddPosition(1, 101, 10, 100m);

            Assert.Throws<ArgumentException>(() =>
                _positionService.ReducePosition(1, 101, quantity));

            Assert.Equal(10, _positionService.GetPosition(1, 101)!.Quantity);
        }

        // TEST 13: Buying again after selling everything creates a new position
        [Fact]
        public void AddPosition_AfterFullSale_CreatesFreshPosition()
        {
            _positionService.AddPosition(1, 101, 10, 100m);

            int oldPositionId =
                _positionService.GetPosition(1, 101)!.PositionId;

            _positionService.ReducePosition(1, 101, 10);

            _positionService.AddPosition(1, 101, 5, 150m);

            var position = _positionService.GetPosition(1, 101);

            Assert.NotNull(position);
            Assert.Equal(5, position.Quantity);
            Assert.Equal(150m, position.AverageCost);
            Assert.NotEqual(oldPositionId, position.PositionId);
        }

        // TEST 14: Positions belonging to different accounts do not interfere
        [Fact]
        public void ReducePosition_DoesNotAffectOtherAccounts()
        {
            _positionService.AddPosition(1, 101, 20, 100m);
            _positionService.AddPosition(2, 101, 30, 200m);

            _positionService.ReducePosition(1, 101, 5);

            Assert.Equal(15, _positionService.GetPosition(1, 101)!.Quantity);
            Assert.Equal(30, _positionService.GetPosition(2, 101)!.Quantity);
        }
    }
}
