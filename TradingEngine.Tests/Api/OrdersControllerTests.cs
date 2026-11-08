using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using TradingEngine.Api.Controllers;
using TradingEngine.Api.DTOs.Order;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Interfaces.Repositories;
using TradingEngine.Core.Services;
using Xunit;

namespace TradingEngine.Tests.Api;

/// <summary>
/// Controller-action tests without Oracle or an HTTP test host.
/// Uses real TradingCoordinator/OrderService with in-memory repositories.
/// Automatic [ApiController] model validation is tested using DataAnnotations,
/// not by direct method calls. The fake UnitOfWork does not simulate rollback.
/// </summary>
public sealed class OrdersControllerTests
{
    private readonly FakeOrderRepository orders = new();
    private readonly FakeAccountRepository accounts = new();
    private readonly FakePositionRepository positions = new();
    private readonly FakeUnitOfWork uow = new();
    private readonly TradingRuntimeState runtime = new();
    private readonly OrderMatchingEngine engine = new();
    private readonly OrdersController controller;

    public OrdersControllerTests()
    {
        // Production starts in recovery mode. Each isolated test fixture starts
        // with an already-recovered book; recovery-specific tests override this.
        runtime.RequiresRecovery = false;

        accounts.Seed(new Account
        {
            AccountId = 7,
            AccountName = "Buyer",
            IsActive = true,
            CashBalance = 20_000m,
            AvailableCash = 20_000m,
            ReservedCash = 0m
        });
        accounts.Seed(new Account
        {
            AccountId = 8,
            AccountName = "Seller",
            IsActive = true,
            CashBalance = 1_000m,
            AvailableCash = 1_000m,
            ReservedCash = 0m
        });
        positions.Seed(new Position
        {
            PositionId = 1,
            AccountId = 8,
            InstrumentId = 10,
            Quantity = 100,
            ReservedQuantity = 0,
            AverageCost = 25m
        });

        var accountService = new AccountService(accounts, uow);
        var positionService = new PositionService(positions, uow);
        var orderService = new OrderService(accountService, engine, positionService, accounts, uow, orders);
        var coordinator = new TradingCoordinator(null!, engine, orderService, orders, runtime);
        controller = new OrdersController(orders, coordinator);
    }

    private static PlaceOrderRequestDto Request(OrderSide side = OrderSide.Buy,
        int accountId = 7, int instrumentId = 10, int quantity = 20, decimal price = 100m) => new()
        {
            AccountId = accountId,
            InstrumentId = instrumentId,
            Side = side,
            Quantity = quantity,
            LimitPrice = price
        };

    private static Order Existing(int id = 101, int accountId = 7,
        int instrumentId = 10, OrderSide side = OrderSide.Buy,
        int quantity = 20, int filled = 0, OrderStatus status = OrderStatus.Pending) => new()
        {
            OrderId = id,
            AccountId = accountId,
            InstrumentId = instrumentId,
            Side = side,
            OrderType = OrderType.Limit,
            Quantity = quantity,
            LimitPrice = 100m,
            FilledQuantity = filled,
            Status = status,
            CreatedAt = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc)
        };

    private static List<ValidationResult> Validate(object value)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(value, new ValidationContext(value), results, true);
        return results;
    }

    [Theory]
    [InlineData(0, 10, 1, 1)]
    [InlineData(7, 0, 1, 1)]
    [InlineData(7, 10, 0, 1)]
    [InlineData(7, 10, -1, 1)]
    [InlineData(7, 10, 1, 0)]
    [InlineData(7, 10, 1, -1)]
    public void Request_InvalidNumbers_FailDataAnnotations(int accountId, int instrumentId,
        int quantity, int price)
    {
        Assert.NotEmpty(Validate(Request(accountId: accountId, instrumentId: instrumentId,
            quantity: quantity, price: price)));
    }

    [Fact]
    public void Request_InvalidSide_FailsDataAnnotations() =>
        Assert.NotEmpty(Validate(Request(side: (OrderSide)999)));

    [Fact]
    public void Request_ValidBuyAndSell_PassDataAnnotations()
    {
        Assert.Empty(Validate(Request()));
        Assert.Empty(Validate(Request(OrderSide.Sell, accountId: 8)));
    }

    // Placement: 200 is asserted to match the uploaded controller.
    // After switching to CreatedAtAction, update this to assert HTTP 201.
    [Fact]
    public async Task PlaceOrder_Buy_ReservesFundsPersistsAndRegistersInBook()
    {
        using var cts = new CancellationTokenSource();
        var result = await controller.PlaceOrder(Request(quantity: 12, price: 125m), cts.Token);

        var dto = Assert.IsType<OrderResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(dto.OrderId > 0);
        Assert.Equal(7, dto.AccountId);
        Assert.Equal(10, dto.InstrumentId);
        Assert.Equal(OrderSide.Buy, dto.Side);
        Assert.Equal(OrderType.Limit, dto.OrderType);
        Assert.Equal(12, dto.Quantity);
        Assert.Equal(125m, dto.LimitPrice);
        Assert.Equal(0, dto.FilledQuantity);
        Assert.Equal(12, dto.RemainingQuantity);
        Assert.Equal(OrderStatus.Pending, dto.Status);
        Assert.True(dto.CreatedAt > DateTime.MinValue);
        Assert.NotNull(engine.GetOrderById(dto.OrderId));
        Assert.Equal(18_500m, accounts.Find(7)!.AvailableCash);
        Assert.Equal(1_500m, accounts.Find(7)!.ReservedCash);
        Assert.Single(orders.All);
        Assert.Equal(1, uow.TransactionCount);
        Assert.Equal(cts.Token, orders.LastAddToken);
    }

    [Fact]
    public async Task PlaceOrder_Sell_ReservesSharesPersistsAndRegistersInBook()
    {
        var result = await controller.PlaceOrder(
            Request(OrderSide.Sell, accountId: 8, quantity: 30, price: 110m), CancellationToken.None);

        var dto = Assert.IsType<OrderResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(OrderSide.Sell, dto.Side);
        Assert.Equal(OrderType.Limit, dto.OrderType);
        Assert.Equal(30, dto.RemainingQuantity);
        Assert.NotNull(engine.GetOrderById(dto.OrderId));
        Assert.Equal(30, positions.Find(8, 10)!.ReservedQuantity);
        Assert.Equal(70, positions.Find(8, 10)!.AvailableQuantity);
        Assert.Equal(1_000m, accounts.Find(8)!.AvailableCash);
        Assert.Single(orders.All);
    }

    [Fact]
    public async Task PlaceOrder_InsufficientCash_ThrowsAndDoesNotAddOrder()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.PlaceOrder(Request(quantity: 300, price: 100m), CancellationToken.None));
        Assert.Empty(orders.All);
        Assert.Equal(0m, accounts.Find(7)!.ReservedCash);
        Assert.False(runtime.RequiresRecovery);
    }

    [Fact]
    public async Task PlaceOrder_InsufficientShares_ThrowsAndDoesNotAddOrder()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.PlaceOrder(Request(OrderSide.Sell, accountId: 8, quantity: 101), CancellationToken.None));
        Assert.Empty(orders.All);
        Assert.Equal(0, positions.Find(8, 10)!.ReservedQuantity);
        Assert.False(runtime.RequiresRecovery);
    }

    [Fact]
    public async Task PlaceOrder_RecoveryRequired_ThrowsWithoutPersisting()
    {
        runtime.RequiresRecovery = true;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.PlaceOrder(Request(), CancellationToken.None));
        Assert.Contains("suspended", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(orders.All);
        Assert.Equal(0, uow.TransactionCount);
    }

    [Fact]
    public async Task PlaceOrder_AlreadyCancelledToken_DoesNotAcquireGateOrPersist()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.PlaceOrder(Request(), cts.Token));
        Assert.Empty(orders.All);
    }

    [Fact]
    public async Task GetOrderById_Exists_Returns200AndMapsEveryField()
    {
        orders.Seed(Existing(quantity: 20, filled: 8, status: OrderStatus.PartiallyFilled));
        var result = await controller.GetOrderById(101, CancellationToken.None);
        var dto = Assert.IsType<OrderResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(101, dto.OrderId);
        Assert.Equal(7, dto.AccountId);
        Assert.Equal(10, dto.InstrumentId);
        Assert.Equal(OrderSide.Buy, dto.Side);
        Assert.Equal(OrderType.Limit, dto.OrderType);
        Assert.Equal(20, dto.Quantity);
        Assert.Equal(100m, dto.LimitPrice);
        Assert.Equal(8, dto.FilledQuantity);
        Assert.Equal(12, dto.RemainingQuantity);
        Assert.Equal(OrderStatus.PartiallyFilled, dto.Status);
        Assert.Equal(new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc), dto.CreatedAt);
    }

    [Fact]
    public async Task GetOrderById_Missing_Returns404()
    {
        var result = await controller.GetOrderById(999, CancellationToken.None);
        Assert.Contains("999", Assert.IsType<string>(Assert.IsType<NotFoundObjectResult>(result.Result).Value));
    }

    [Fact]
    public async Task GetOrderById_ForwardsIdAndToken()
    {
        using var cts = new CancellationTokenSource();
        await controller.GetOrderById(123, cts.Token);
        Assert.Equal(123, orders.LastId);
        Assert.Equal(cts.Token, orders.LastReadToken);
    }

    [Fact]
    public async Task GetOrderById_RepositoryFailure_Propagates()
    {
        orders.Failure = new InvalidOperationException("Oracle unavailable");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.GetOrderById(101, CancellationToken.None));
        Assert.Equal("Oracle unavailable", ex.Message);
    }

    [Fact]
    public async Task GetOrderById_ModifyingDto_DoesNotModifyEntity()
    {
        var entity = Existing();
        orders.Seed(entity);
        var response = await controller.GetOrderById(entity.OrderId, CancellationToken.None);
        var dto = Assert.IsType<OrderResponseDto>(Assert.IsType<OkObjectResult>(response.Result).Value);
        dto.Quantity = 999;
        dto.Status = OrderStatus.Cancelled;
        Assert.Equal(20, entity.Quantity);
        Assert.Equal(OrderStatus.Pending, entity.Status);
    }

    [Fact]
    public async Task GetOrderByAccount_Empty_Returns200WithEmptyList()
    {
        var result = await controller.GetOrderByAccount(7, CancellationToken.None);
        Assert.Empty(Assert.IsType<List<OrderResponseDto>>(Assert.IsType<OkObjectResult>(result.Result).Value));
    }

    [Fact]
    public async Task GetOrderByAccount_OnlyRequestedAccount_ReturnsMappedOrders()
    {
        orders.Seed(Existing(id: 101));
        orders.Seed(Existing(id: 102, status: OrderStatus.Filled, filled: 20));
        orders.Seed(Existing(id: 103, accountId: 8, side: OrderSide.Sell));
        var result = await controller.GetOrderByAccount(7, CancellationToken.None);
        var dtos = Assert.IsType<List<OrderResponseDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(new[] { 101, 102 }, dtos.Select(o => o.OrderId));
        Assert.All(dtos, o => Assert.Equal(7, o.AccountId));
        Assert.Equal(0, dtos[1].RemainingQuantity);
        Assert.Equal(OrderStatus.Filled, dtos[1].Status);
    }

    [Fact]
    public async Task GetOrderByAccount_ForwardsIdAndToken()
    {
        using var cts = new CancellationTokenSource();
        await controller.GetOrderByAccount(7, cts.Token);
        Assert.Equal(7, orders.LastAccountId);
        Assert.Equal(cts.Token, orders.LastReadToken);
    }

    [Fact]
    public async Task GetOpenOrdersByInstrument_Empty_Returns200WithEmptyList()
    {
        var result = await controller.GetOpenOrdersByInstrument(10, CancellationToken.None);
        Assert.Empty(Assert.IsType<List<OrderResponseDto>>(Assert.IsType<OkObjectResult>(result.Result).Value));
    }

    [Fact]
    public async Task GetOpenOrdersByInstrument_ExcludesFilledCancelledOtherInstruments()
    {
        orders.Seed(Existing(id: 101, status: OrderStatus.Pending));
        orders.Seed(Existing(id: 102, status: OrderStatus.PartiallyFilled, filled: 5));
        orders.Seed(Existing(id: 103, status: OrderStatus.Filled, filled: 20));
        orders.Seed(Existing(id: 104, status: OrderStatus.Cancelled));
        orders.Seed(Existing(id: 105, instrumentId: 11));
        var result = await controller.GetOpenOrdersByInstrument(10, CancellationToken.None);
        var dtos = Assert.IsType<List<OrderResponseDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(new[] { 101, 102 }, dtos.Select(o => o.OrderId));
        Assert.Equal(15, dtos[1].RemainingQuantity);
    }

    [Fact]
    public async Task GetOpenOrdersByInstrument_ForwardsIdAndToken()
    {
        using var cts = new CancellationTokenSource();
        await controller.GetOpenOrdersByInstrument(15, cts.Token);
        Assert.Equal(15, orders.LastInstrumentId);
        Assert.Equal(cts.Token, orders.LastReadToken);
    }

    [Fact]
    public async Task CancelOrder_Missing_Returns404WithoutTransaction()
    {
        var result = await controller.CancelOrder(999, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(0, uow.TransactionCount);
    }

    [Fact]
    public async Task CancelOrder_Buy_ReleasesRemainingCashAndCancelsBookOrder()
    {
        var placed = await controller.PlaceOrder(Request(quantity: 20, price: 100m), CancellationToken.None);
        var dto = Assert.IsType<OrderResponseDto>(Assert.IsType<OkObjectResult>(placed.Result).Value);
        var result = await controller.CancelOrder(dto.OrderId, CancellationToken.None);
        Assert.IsType<OkResult>(result); // Current ZIP: change to NoContentResult after HTTP 204 fix.
        Assert.Equal(OrderStatus.Cancelled, orders.Find(dto.OrderId)!.Status);
        Assert.Equal(OrderStatus.Cancelled, engine.GetOrderById(dto.OrderId)!.Status);
        Assert.Equal(20_000m, accounts.Find(7)!.AvailableCash);
        Assert.Equal(0m, accounts.Find(7)!.ReservedCash);
    }

    [Fact]
    public async Task CancelOrder_Sell_ReleasesSharesAndCancelsBookOrder()
    {
        var placed = await controller.PlaceOrder(
            Request(OrderSide.Sell, accountId: 8, quantity: 30), CancellationToken.None);
        var dto = Assert.IsType<OrderResponseDto>(Assert.IsType<OkObjectResult>(placed.Result).Value);
        var result = await controller.CancelOrder(dto.OrderId, CancellationToken.None);
        Assert.IsType<OkResult>(result);
        Assert.Equal(OrderStatus.Cancelled, orders.Find(dto.OrderId)!.Status);
        Assert.Equal(OrderStatus.Cancelled, engine.GetOrderById(dto.OrderId)!.Status);
        Assert.Equal(0, positions.Find(8, 10)!.ReservedQuantity);
        Assert.Equal(100, positions.Find(8, 10)!.AvailableQuantity);
    }

    [Fact]
    public async Task CancelOrder_AlreadyCancelled_ThrowsAndDoesNotReleaseTwice()
    {
        var placed = await controller.PlaceOrder(Request(), CancellationToken.None);
        var dto = Assert.IsType<OrderResponseDto>(Assert.IsType<OkObjectResult>(placed.Result).Value);
        await controller.CancelOrder(dto.OrderId, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.CancelOrder(dto.OrderId, CancellationToken.None));
        Assert.Equal(0m, accounts.Find(7)!.ReservedCash);
        Assert.False(runtime.RequiresRecovery);
    }

    [Fact]
    public async Task CancelOrder_RecoveryRequired_DoesNotCancel()
    {
        var placed = await controller.PlaceOrder(Request(), CancellationToken.None);
        var dto = Assert.IsType<OrderResponseDto>(Assert.IsType<OkObjectResult>(placed.Result).Value);
        runtime.RequiresRecovery = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.CancelOrder(dto.OrderId, CancellationToken.None));
        Assert.Equal(OrderStatus.Pending, orders.Find(dto.OrderId)!.Status);
        Assert.Equal(OrderStatus.Pending, engine.GetOrderById(dto.OrderId)!.Status);
    }

    [Fact]
    public async Task CancelOrder_AlreadyCancelledToken_DoesNotCancel()
    {
        var placed = await controller.PlaceOrder(Request(), CancellationToken.None);
        var dto = Assert.IsType<OrderResponseDto>(Assert.IsType<OkObjectResult>(placed.Result).Value);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.CancelOrder(dto.OrderId, cts.Token));
        Assert.Equal(OrderStatus.Pending, orders.Find(dto.OrderId)!.Status);
        Assert.Equal(OrderStatus.Pending, engine.GetOrderById(dto.OrderId)!.Status);
        Assert.Equal(2_000m, accounts.Find(7)!.ReservedCash);
        Assert.Equal(18_000m, accounts.Find(7)!.AvailableCash);
        Assert.False(runtime.RequiresRecovery);
    }

    // The existing action currently declares ActionResult<OrderResponseDto>
    // despite returning a list inside OkObjectResult. Verify the runtime payload
    // here; change the action signature to ActionResult<List<OrderResponseDto>>
    // separately as part of API contract cleanup.
    [Fact]
    public async Task GetOpenOrdersByInstrument_ReturnsListPayload()
    {
        orders.Seed(Existing(id: 101));
        var result = await controller.GetOpenOrdersByInstrument(10, CancellationToken.None);
        var payload = Assert.IsType<List<OrderResponseDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Single(payload);
        Assert.Equal(101, payload[0].OrderId);
    }

    private sealed class FakeOrderRepository : IOrderRepository
    {
        private readonly List<Order> data = new();
        private int nextId = 100;
        public IReadOnlyList<Order> All => data;
        public Exception? Failure { get; set; }
        public int LastId { get; private set; }
        public int LastAccountId { get; private set; }
        public int LastInstrumentId { get; private set; }
        public CancellationToken LastReadToken { get; private set; }
        public CancellationToken LastAddToken { get; private set; }
        public Order? Find(int id) => data.SingleOrDefault(o => o.OrderId == id);
        public void Seed(Order order) { data.Add(order); nextId = Math.Max(nextId, order.OrderId); }
        public Task AddAsync(Order order, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Failure != null) throw Failure;
            order.OrderId = ++nextId;
            data.Add(order);
            LastAddToken = ct;
            return Task.CompletedTask;
        }
        public Task<Order?> GetByIdAsync(int orderId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Failure != null) throw Failure;
            LastId = orderId; LastReadToken = ct;
            return Task.FromResult(Find(orderId));
        }
        public Task<List<Order>> GetByAccountIdAsync(int accountId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Failure != null) throw Failure;
            LastAccountId = accountId; LastReadToken = ct;
            return Task.FromResult(data.Where(o => o.AccountId == accountId).ToList());
        }
        public Task<List<Order>> GetOpenOrdersByInstrumentAsync(int instrumentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Failure != null) throw Failure;
            LastInstrumentId = instrumentId; LastReadToken = ct;
            return Task.FromResult(data.Where(o => o.InstrumentId == instrumentId &&
                (o.Status == OrderStatus.Pending || o.Status == OrderStatus.PartiallyFilled)).ToList());
        }
        public Task<List<Order>> GetAllOpenOrdersAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(data.Where(o =>
                o.Status == OrderStatus.Pending || o.Status == OrderStatus.PartiallyFilled).ToList());
        }
    }

    private sealed class FakeAccountRepository : IAccountRepository
    {
        private readonly Dictionary<int, Account> data = new();
        public void Seed(Account account) => data[account.AccountId] = account;
        public Account? Find(int id) => data.GetValueOrDefault(id);
        public Task<Account?> GetByIdAsync(int accountId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Find(accountId));
        }
        public Task AddAsync(Account account, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            data[account.AccountId] = account;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePositionRepository : IPositionRepository
    {
        private readonly List<Position> data = new();
        public void Seed(Position position) => data.Add(position);
        public Position? Find(int accountId, int instrumentId) => data.SingleOrDefault(p =>
            p.AccountId == accountId && p.InstrumentId == instrumentId);
        public Task<Position?> GetByAccountAndInstrumentAsync(int accountId, int instrumentId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Find(accountId, instrumentId));
        }
        public Task AddAsync(Position position, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            data.Add(position);
            return Task.CompletedTask;
        }
        public Task<List<Position>> GetByAccountIdAsync(int accountId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(data.Where(p => p.AccountId == accountId).ToList());
        }
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int TransactionCount { get; private set; }
        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(1);
        }
        public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            TransactionCount++;
            return await work(ct);
        }
    }
}
