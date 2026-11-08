
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Services;
using Xunit;

public class OrderMatchingEnginePersistenceTests
{
    private readonly OrderMatchingEngine engine = new();

    private static Order CreateOrder(
        int orderId,
        OrderSide side = OrderSide.Buy,
        int quantity = 100,
        int filledQuantity = 0,
        OrderStatus status = OrderStatus.Pending)
    {
        return new Order
        {
            OrderId = orderId,
            AccountId = 1,
            InstrumentId = 10,
            Side = side,
            OrderType = OrderType.Limit,
            Quantity = quantity,
            LimitPrice = 100m,
            FilledQuantity = filledQuantity,
            Status = status,
            CreatedAt = DateTime.UtcNow
        };
    }

    [Fact]
    public void RegisterPersistedOrder_PreservesDatabaseId()
    {
        Order order = CreateOrder(105);

        Order registered = engine.RegisterPersistedOrder(order);

        Assert.Equal(105, registered.OrderId);
        Assert.NotNull(engine.GetOrderById(105));
    }

    [Fact]
    public void RegisterPersistedOrder_PreservesPartialFill()
    {
        Order order = CreateOrder(
            106,
            OrderSide.Buy,
            100,
            40,
            OrderStatus.PartiallyFilled);

        Order registered = engine.RegisterPersistedOrder(order);

        Assert.Equal(40, registered.FilledQuantity);
        Assert.Equal(OrderStatus.PartiallyFilled, registered.Status);
        Assert.Equal(100, registered.Quantity);
    }

    [Fact]
    public void RegisterPersistedOrder_DuplicateId_Throws()
    {
        engine.RegisterPersistedOrder(CreateOrder(107));

        Assert.Throws<InvalidOperationException>(() =>
            engine.RegisterPersistedOrder(CreateOrder(107)));
    }

    [Fact]
    public void RegisterPersistedOrder_WithoutDatabaseId_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            engine.RegisterPersistedOrder(CreateOrder(0)));
    }

    [Fact]
    public void RegisterPersistedOrder_CancelledOrder_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            engine.RegisterPersistedOrder(
                CreateOrder(
                    108,
                    status: OrderStatus.Cancelled)));
    }

    [Fact]
    public void RegisterPersistedOrder_DoesNotMutateOriginal()
    {
        Order original = CreateOrder(109);

        engine.RegisterPersistedOrder(original);

        original.Quantity = 500;
        original.Status = OrderStatus.Cancelled;

        Order? stored = engine.GetOrderById(109);

        Assert.NotNull(stored);
        Assert.Equal(100, stored.Quantity);
        Assert.Equal(OrderStatus.Pending, stored.Status);
    }

    [Fact]
    public void RegisterPersistedOrder_OrdersCanBeCancelled()
    {
        engine.RegisterPersistedOrder(CreateOrder(110));

        bool result = engine.CancelOrder(110);

        Assert.True(result);
        Assert.Equal(
            OrderStatus.Cancelled,
            engine.GetOrderById(110)!.Status);
    }
}
