using System;
using Xunit;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Services;

namespace TradingEngine.Tests;

public class ProposeAndConfirmMatchTests
{
    private const int InstrumentId = 10;
    private static readonly DateTime BaseTime = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly OrderMatchingEngine _engine = new();

    private static Order NewOrder(
        int id, OrderSide side, int quantity = 100, decimal price = 100m,
        int filled = 0, int seconds = 0, int instrumentId = InstrumentId)
    {
        return new Order
        {
            OrderId = id,
            AccountId = side == OrderSide.Buy ? 1 : 2,
            InstrumentId = instrumentId,
            Side = side,
            OrderType = OrderType.Limit,
            Quantity = quantity,
            LimitPrice = price,
            FilledQuantity = filled,
            Status = filled == 0 ? OrderStatus.Pending : OrderStatus.PartiallyFilled,
            CreatedAt = BaseTime.AddSeconds(seconds)
        };
    }

    private void Load(params Order[] orders) => _engine.RebuildFromPersistedOrders(orders);

    private void AssertState(int id, int filled, OrderStatus status)
    {
        Order? order = _engine.GetOrderById(id);
        Assert.NotNull(order);
        Assert.Equal(filled, order.FilledQuantity);
        Assert.Equal(status, order.Status);
    }

    // PROPOSE MATCH

    [Fact]
    public void ProposeMatch_NoOrderBook_ReturnsNull()
    {
        Assert.Null(_engine.ProposeMatch(InstrumentId));
    }

    [Fact]
    public void ProposeMatch_OnlyBuyOrder_ReturnsNull()
    {
        Load(NewOrder(101, OrderSide.Buy));
        Assert.Null(_engine.ProposeMatch(InstrumentId));
    }

    [Fact]
    public void ProposeMatch_OnlySellOrder_ReturnsNull()
    {
        Load(NewOrder(102, OrderSide.Sell));
        Assert.Null(_engine.ProposeMatch(InstrumentId));
    }

    [Fact]
    public void ProposeMatch_NonCrossingPrices_ReturnsNullWithoutMutatingOrders()
    {
        Load(NewOrder(101, OrderSide.Buy, price: 99m),
             NewOrder(102, OrderSide.Sell, price: 100m));

        Assert.Null(_engine.ProposeMatch(InstrumentId));
        AssertState(101, 0, OrderStatus.Pending);
        AssertState(102, 0, OrderStatus.Pending);
    }

    [Fact]
    public void ProposeMatch_EqualPrices_ProposesFullFill()
    {
        Load(NewOrder(101, OrderSide.Buy, quantity: 50, price: 100m),
             NewOrder(102, OrderSide.Sell, quantity: 50, price: 100m, seconds: 1));

        ProposedMatch? match = _engine.ProposeMatch(InstrumentId);

        Assert.NotNull(match);
        Assert.Equal(101, match.BuyOrderId);
        Assert.Equal(102, match.SellOrderId);
        Assert.Equal(InstrumentId, match.InstrumentId);
        Assert.Equal(50, match.Quantity);
        Assert.Equal(100m, match.Price);
        AssertState(101, 0, OrderStatus.Pending);
        AssertState(102, 0, OrderStatus.Pending);
    }

    [Fact]
    public void ProposeMatch_PartialQuantity_UsesMinimumRemainingQuantity()
    {
        Load(NewOrder(101, OrderSide.Buy, quantity: 100, filled: 20, price: 105m),
             NewOrder(102, OrderSide.Sell, quantity: 45, price: 100m, seconds: 1));

        ProposedMatch? match = _engine.ProposeMatch(InstrumentId);

        Assert.NotNull(match);
        Assert.Equal(45, match.Quantity);
        AssertState(101, 20, OrderStatus.PartiallyFilled);
        AssertState(102, 0, OrderStatus.Pending);
    }

    [Theory]
    [InlineData(true, 105)]
    [InlineData(false, 100)]
    public void ProposeMatch_UsesOlderOrdersPrice(bool buyOlder, int expectedPrice)
    {
        Load(NewOrder(101, OrderSide.Buy, price: 105m, seconds: buyOlder ? 0 : 2),
             NewOrder(102, OrderSide.Sell, price: 100m, seconds: buyOlder ? 2 : 0));

        ProposedMatch? match = _engine.ProposeMatch(InstrumentId);

        Assert.NotNull(match);
        Assert.Equal((decimal)expectedPrice, match.Price);
    }

    [Fact]
    public void ProposeMatch_SameTimestamps_UsesLowerOrderIdForPrice()
    {
        Load(NewOrder(201, OrderSide.Buy, price: 105m),
             NewOrder(101, OrderSide.Sell, price: 100m));

        Assert.Equal(100m, _engine.ProposeMatch(InstrumentId)!.Price);
    }

    [Fact]
    public void ProposeMatch_CalledTwice_ReturnsSameProposalWithoutMutating()
    {
        Load(NewOrder(101, OrderSide.Buy, price: 105m),
             NewOrder(102, OrderSide.Sell, price: 100m));

        ProposedMatch? first = _engine.ProposeMatch(InstrumentId);
        ProposedMatch? second = _engine.ProposeMatch(InstrumentId);

        Assert.NotNull(first);
        Assert.Equal(first, second); // Record value equality
        AssertState(101, 0, OrderStatus.Pending);
        AssertState(102, 0, OrderStatus.Pending);
    }

    [Fact]
    public void ProposeMatch_CancelledTopBuy_SkipsItAndChoosesNextBuy()
    {
        Load(NewOrder(101, OrderSide.Buy, price: 110m),
             NewOrder(103, OrderSide.Buy, price: 105m, seconds: 1),
             NewOrder(102, OrderSide.Sell, price: 100m, seconds: 2));
        Assert.True(_engine.CancelOrder(101));

        ProposedMatch? match = _engine.ProposeMatch(InstrumentId);

        Assert.NotNull(match);
        Assert.Equal(103, match.BuyOrderId);
        AssertState(101, 0, OrderStatus.Cancelled);
    }

    [Fact]
    public void ProposeMatch_UsesHighestBidAndLowestAsk()
    {
        Load(NewOrder(101, OrderSide.Buy, price: 103m),
             NewOrder(103, OrderSide.Buy, price: 105m, seconds: 1),
             NewOrder(102, OrderSide.Sell, price: 102m, seconds: 2),
             NewOrder(104, OrderSide.Sell, price: 100m, seconds: 3));

        ProposedMatch? match = _engine.ProposeMatch(InstrumentId);

        Assert.NotNull(match);
        Assert.Equal(103, match.BuyOrderId);
        Assert.Equal(104, match.SellOrderId);
    }

    // CONFIRM MATCH

    [Fact]
    public void ConfirmMatch_FullFill_MarksBothFilledAndRemovesFromExecutableBook()
    {
        Load(NewOrder(101, OrderSide.Buy, quantity: 50, price: 105m),
             NewOrder(102, OrderSide.Sell, quantity: 50, price: 100m, seconds: 1));
        ProposedMatch match = _engine.ProposeMatch(InstrumentId)!;

        _engine.ConfirmMatch(match);

        AssertState(101, 50, OrderStatus.Filled);
        AssertState(102, 50, OrderStatus.Filled);
        Assert.Null(_engine.ProposeMatch(InstrumentId));
    }

    [Fact]
    public void ConfirmMatch_PartialBuy_RemainsEligibleForNextSell()
    {
        Load(NewOrder(101, OrderSide.Buy, quantity: 100, price: 105m),
             NewOrder(102, OrderSide.Sell, quantity: 40, price: 100m, seconds: 1),
             NewOrder(103, OrderSide.Sell, quantity: 60, price: 101m, seconds: 2));

        _engine.ConfirmMatch(_engine.ProposeMatch(InstrumentId)!);

        AssertState(101, 40, OrderStatus.PartiallyFilled);
        AssertState(102, 40, OrderStatus.Filled);
        ProposedMatch? next = _engine.ProposeMatch(InstrumentId);
        Assert.NotNull(next);
        Assert.Equal(101, next.BuyOrderId);
        Assert.Equal(103, next.SellOrderId);
        Assert.Equal(60, next.Quantity);
    }

    [Fact]
    public void ConfirmMatch_PartialSell_RemainsEligibleForNextBuy()
    {
        Load(NewOrder(101, OrderSide.Buy, quantity: 40, price: 105m),
             NewOrder(103, OrderSide.Buy, quantity: 60, price: 104m, seconds: 1),
             NewOrder(102, OrderSide.Sell, quantity: 100, price: 100m, seconds: 2));

        _engine.ConfirmMatch(_engine.ProposeMatch(InstrumentId)!);

        AssertState(101, 40, OrderStatus.Filled);
        AssertState(102, 40, OrderStatus.PartiallyFilled);
        ProposedMatch? next = _engine.ProposeMatch(InstrumentId);
        Assert.NotNull(next);
        Assert.Equal(103, next.BuyOrderId);
        Assert.Equal(102, next.SellOrderId);
        Assert.Equal(60, next.Quantity);
    }

    [Fact]
    public void ConfirmMatch_RepeatedConfirmation_ThrowsAndDoesNotDoubleFill()
    {
        Load(NewOrder(101, OrderSide.Buy, quantity: 100, price: 105m),
             NewOrder(102, OrderSide.Sell, quantity: 40, price: 100m));
        ProposedMatch match = _engine.ProposeMatch(InstrumentId)!;
        _engine.ConfirmMatch(match);

        Assert.Throws<InvalidOperationException>(() => _engine.ConfirmMatch(match));
        AssertState(101, 40, OrderStatus.PartiallyFilled);
        AssertState(102, 40, OrderStatus.Filled);
    }

    [Fact]
    public void ConfirmMatch_StaleAfterCancellation_ThrowsWithoutFilling()
    {
        Load(NewOrder(101, OrderSide.Buy, price: 105m),
             NewOrder(102, OrderSide.Sell, price: 100m));
        ProposedMatch match = _engine.ProposeMatch(InstrumentId)!;
        Assert.True(_engine.CancelOrder(101));

        Assert.Throws<InvalidOperationException>(() => _engine.ConfirmMatch(match));
        AssertState(101, 0, OrderStatus.Cancelled);
        AssertState(102, 0, OrderStatus.Pending);
    }

    [Fact]
    public void ConfirmMatch_WrongBuyOrderId_ThrowsWithoutFilling()
    {
        Load(NewOrder(101, OrderSide.Buy, price: 105m),
             NewOrder(102, OrderSide.Sell, price: 100m));
        ProposedMatch match = _engine.ProposeMatch(InstrumentId)! with { BuyOrderId = 999 };

        Assert.Throws<InvalidOperationException>(() => _engine.ConfirmMatch(match));
        AssertState(101, 0, OrderStatus.Pending);
        AssertState(102, 0, OrderStatus.Pending);
    }

    [Fact]
    public void ConfirmMatch_WrongSellOrderId_ThrowsWithoutFilling()
    {
        Load(NewOrder(101, OrderSide.Buy, price: 105m),
             NewOrder(102, OrderSide.Sell, price: 100m));
        ProposedMatch match = _engine.ProposeMatch(InstrumentId)! with { SellOrderId = 999 };

        Assert.Throws<InvalidOperationException>(() => _engine.ConfirmMatch(match));
        AssertState(101, 0, OrderStatus.Pending);
        AssertState(102, 0, OrderStatus.Pending);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(41)]
    public void ConfirmMatch_InvalidQuantity_ThrowsWithoutFilling(int quantity)
    {
        Load(NewOrder(101, OrderSide.Buy, quantity: 100, price: 105m),
             NewOrder(102, OrderSide.Sell, quantity: 40, price: 100m));
        ProposedMatch match = _engine.ProposeMatch(InstrumentId)! with { Quantity = quantity };

        Assert.Throws<InvalidOperationException>(() => _engine.ConfirmMatch(match));
        AssertState(101, 0, OrderStatus.Pending);
        AssertState(102, 0, OrderStatus.Pending);
    }

    [Fact]
    public void ConfirmMatch_WrongPrice_ThrowsWithoutFilling()
    {
        Load(NewOrder(101, OrderSide.Buy, price: 105m),
             NewOrder(102, OrderSide.Sell, price: 100m, seconds: 1));
        ProposedMatch match = _engine.ProposeMatch(InstrumentId)! with { Price = 999m };

        Assert.Throws<InvalidOperationException>(() => _engine.ConfirmMatch(match));
        AssertState(101, 0, OrderStatus.Pending);
        AssertState(102, 0, OrderStatus.Pending);
    }

    [Fact]
    public void ConfirmMatch_MissingOrderBook_Throws()
    {
        var fakeMatch = new ProposedMatch(101, 102, 999, 10, 100m);
        Assert.Throws<InvalidOperationException>(() => _engine.ConfirmMatch(fakeMatch));
    }

    [Fact]
    public void ConfirmMatch_NullProposal_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _engine.ConfirmMatch(null!));
    }

    [Fact]
    public void ConfirmMatch_AfterRebuildWithDifferentTopOrder_RejectsStaleProposal()
    {
        Load(NewOrder(101, OrderSide.Buy, price: 105m),
             NewOrder(102, OrderSide.Sell, price: 100m));
        ProposedMatch stale = _engine.ProposeMatch(InstrumentId)!;

        Load(NewOrder(103, OrderSide.Buy, price: 110m),
             NewOrder(101, OrderSide.Buy, price: 105m),
             NewOrder(102, OrderSide.Sell, price: 100m));

        Assert.Throws<InvalidOperationException>(() => _engine.ConfirmMatch(stale));
        AssertState(103, 0, OrderStatus.Pending);
        AssertState(101, 0, OrderStatus.Pending);
        AssertState(102, 0, OrderStatus.Pending);
    }
}
