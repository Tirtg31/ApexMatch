using Microsoft.AspNetCore.Mvc;
using TradingEngine.Api.Controllers;
using TradingEngine.Api.DTOs.Execution;
using TradingEngine.Api.DTOs.Trading;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Interfaces.Repositories;
using TradingEngine.Core.Services;
using Xunit;

namespace TradingEngine.Tests.Api;

/// <summary>
/// Tests MatchController against real matching/coordinator/settlement logic, using
/// in-memory repositories. No Oracle and no ASP.NET TestServer required.
/// The fake UoW does not model transaction rollback; Oracle atomicity belongs
/// in the existing persistence integration tests.
/// Tests target Match returning a single MatchResultDto containing Executions.
/// </summary>
public sealed class MatchControllerTests
{
    private readonly OrderRepo orders = new();
    private readonly ExecutionRepo executions = new();
    private readonly AccountRepo accounts = new();
    private readonly PositionRepo positions = new();
    private readonly FakeUnitOfWork uow = new();
    private readonly TradingRuntimeState runtime = new();
    private readonly OrderMatchingEngine engine = new();
    private readonly MatchController controller;

    public MatchControllerTests()
    {
        runtime.RequiresRecovery = false; // Production starts true; fixture represents recovered startup.

        accounts.Seed(new Account
        {
            AccountId = 7,
            AccountName = "Buyer",
            IsActive = true,
            CashBalance = 20_000m,
            AvailableCash = 18_000m,
            ReservedCash = 2_000m
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
            ReservedQuantity = 20,
            AverageCost = 75m
        });

        var accountService = new AccountService(accounts, uow);
        var positionService = new PositionService(positions, uow);
        var settlement = new SettlementService(accountService, positionService, uow, orders, executions);
        var orderService = new OrderService(accountService, engine, positionService, accounts, uow, orders);
        var coordinator = new TradingCoordinator(settlement, engine, orderService, orders, runtime);
        controller = new MatchController(coordinator, executions);
    }

    private static Order Order(int id, OrderSide side, int quantity = 20, decimal price = 100m,
        int instrumentId = 10, int filled = 0, OrderStatus status = OrderStatus.Pending,
        int? accountId = null, int timeOffsetSeconds = 0) => new()
        {
            OrderId = id,
            AccountId = accountId ?? (side == OrderSide.Buy ? 7 : 8),
            InstrumentId = instrumentId,
            Side = side,
            OrderType = OrderType.Limit,
            Quantity = quantity,
            LimitPrice = price,
            FilledQuantity = filled,
            Status = status,
            CreatedAt = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc).AddSeconds(timeOffsetSeconds)
        };

    private void SeedOrders(params Order[] values)
    {
        foreach (var order in values)
        {
            orders.Seed(order);
            engine.RegisterPersistedOrder(order);
        }
    }

    private static List<ExecutionResponseDto> AsExecutionList(
        ActionResult<List<ExecutionResponseDto>> result)
    {
        if (result.Result is OkObjectResult ok)
            return Assert.IsType<List<ExecutionResponseDto>>(ok.Value);
        return Assert.IsType<List<ExecutionResponseDto>>(result.Value);
    }

    private static MatchResultDto AsMatchResult(ActionResult<MatchResultDto> result)
    {
        if (result.Result is OkObjectResult ok)
            return Assert.IsType<MatchResultDto>(ok.Value);
        return Assert.IsType<MatchResultDto>(result.Value);
    }

    private static List<ExecutionResponseDto> MatchExecutions(ActionResult<MatchResultDto> result)
        => AsMatchResult(result).Executions;

    [Fact]
    public async Task Match_CrossingOrders_SettlesPersistsAndMapsExecution()
    {
        SeedOrders(Order(101, OrderSide.Buy), Order(102, OrderSide.Sell, price: 90m, timeOffsetSeconds: 1));

        var result = await controller.Match(10, CancellationToken.None);

        var match = AsMatchResult(result);
        Assert.Equal(10, match.InstrumentId);
        Assert.Equal(1, match.TotalExecutions);
        Assert.Equal(20, match.TotalExecutedQuantity);
        Assert.Equal(2_000m, match.TotalTradedValue);
        var dto = Assert.Single(match.Executions);
        Assert.True(dto.ExecutionId > 0);
        Assert.Equal(101, dto.BuyOrderId);
        Assert.Equal(102, dto.SellOrderId);
        Assert.Equal(20, dto.ExecutedQuantity);
        Assert.Equal(100m, dto.ExecutedPrice); // Earlier BUY is the maker.
        Assert.True(dto.ExecutedAt > DateTime.MinValue);
        Assert.Single(executions.All);
        Assert.Equal(OrderStatus.Filled, orders.Find(101)!.Status);
        Assert.Equal(OrderStatus.Filled, orders.Find(102)!.Status);
        Assert.Equal(20, orders.Find(101)!.FilledQuantity);
        Assert.Equal(20, orders.Find(102)!.FilledQuantity);
        Assert.Equal(OrderStatus.Filled, engine.GetOrderById(101)!.Status);
        Assert.Equal(OrderStatus.Filled, engine.GetOrderById(102)!.Status);
        Assert.Equal(18_000m, accounts.Find(7)!.CashBalance);
        Assert.Equal(18_000m, accounts.Find(7)!.AvailableCash);
        Assert.Equal(0m, accounts.Find(7)!.ReservedCash);
        Assert.Equal(3_000m, accounts.Find(8)!.CashBalance);
        Assert.Equal(20, positions.Find(7, 10)!.Quantity);
        Assert.Equal(80, positions.Find(8, 10)!.Quantity);
        Assert.Equal(0, positions.Find(8, 10)!.ReservedQuantity);
        Assert.False(runtime.RequiresRecovery);
        Assert.Equal(1, uow.TransactionCount);
    }

    [Fact]
    public async Task Match_NonCrossingOrders_ReturnsEmptyAndDoesNotSettle()
    {
        SeedOrders(Order(101, OrderSide.Buy, price: 80m),
            Order(102, OrderSide.Sell, price: 90m, timeOffsetSeconds: 1));

        var result = await controller.Match(10, CancellationToken.None);

        var match = AsMatchResult(result);
        Assert.Equal(0, match.TotalExecutions);
        Assert.Equal(0, match.TotalExecutedQuantity);
        Assert.Equal(0m, match.TotalTradedValue);
        Assert.Empty(match.Executions);
        Assert.Empty(executions.All);
        Assert.Equal(0, uow.TransactionCount);
        Assert.Equal(OrderStatus.Pending, orders.Find(101)!.Status);
        Assert.Equal(OrderStatus.Pending, engine.GetOrderById(102)!.Status);
        Assert.False(runtime.RequiresRecovery);
    }

    [Fact]
    public async Task Match_EmptyBook_ReturnsEmpty()
    {
        Assert.Empty(MatchExecutions(await controller.Match(10, CancellationToken.None)));
        Assert.Empty(executions.All);
        Assert.Equal(0, uow.TransactionCount);
    }

    [Fact]
    public async Task Match_OnlyBuyOrders_ReturnsEmpty()
    {
        SeedOrders(Order(101, OrderSide.Buy));
        Assert.Empty(MatchExecutions(await controller.Match(10, CancellationToken.None)));
        Assert.Equal(OrderStatus.Pending, orders.Find(101)!.Status);
    }

    [Fact]
    public async Task Match_SellOlderThanBuy_ExecutesAtOlderSellPrice()
    {
        SeedOrders(Order(101, OrderSide.Sell, price: 90m),
            Order(102, OrderSide.Buy, price: 100m, timeOffsetSeconds: 1));
        var result = await controller.Match(10, CancellationToken.None);
        var dto = Assert.Single(MatchExecutions(result));
        Assert.Equal(90m, dto.ExecutedPrice);
        Assert.Equal(102, dto.BuyOrderId);
        Assert.Equal(101, dto.SellOrderId);
        // Buyer reserved 2000 but pays 1800, so 200 is released.
        Assert.Equal(18_200m, accounts.Find(7)!.AvailableCash);
        Assert.Equal(18_200m, accounts.Find(7)!.CashBalance);
    }

    [Fact]
    public async Task Match_PartialFill_LeavesBuyOpenAndFullyFillsSell()
    {
        SeedOrders(Order(101, OrderSide.Buy, quantity: 20),
            Order(102, OrderSide.Sell, quantity: 10, price: 90m, timeOffsetSeconds: 1));

        var dto = Assert.Single(MatchExecutions(await controller.Match(10, CancellationToken.None)));

        Assert.Equal(10, dto.ExecutedQuantity);
        Assert.Equal(10, orders.Find(101)!.FilledQuantity);
        Assert.Equal(OrderStatus.PartiallyFilled, orders.Find(101)!.Status);
        Assert.Equal(OrderStatus.Filled, orders.Find(102)!.Status);
        Assert.Equal(OrderStatus.PartiallyFilled, engine.GetOrderById(101)!.Status);
        Assert.Equal(OrderStatus.Filled, engine.GetOrderById(102)!.Status);
        Assert.Equal(1_000m, accounts.Find(7)!.ReservedCash);
        Assert.Equal(10, positions.Find(8, 10)!.ReservedQuantity);
        Assert.Equal(1, uow.TransactionCount);
    }

    [Fact]
    public async Task Match_MultipleSells_ProducesMultipleExecutions()
    {
        SeedOrders(Order(101, OrderSide.Buy, quantity: 20),
            Order(102, OrderSide.Sell, quantity: 10, price: 90m, timeOffsetSeconds: 1),
            Order(103, OrderSide.Sell, quantity: 10, price: 95m, timeOffsetSeconds: 2));

        var dtos = MatchExecutions(await controller.Match(10, CancellationToken.None));

        Assert.Equal(2, dtos.Count);
        Assert.Equal(20, dtos.Sum(x => x.ExecutedQuantity));
        Assert.Equal(2, executions.All.Count);
        Assert.Equal(OrderStatus.Filled, orders.Find(101)!.Status);
        Assert.Equal(OrderStatus.Filled, orders.Find(102)!.Status);
        Assert.Equal(OrderStatus.Filled, orders.Find(103)!.Status);
        Assert.Equal(2, uow.TransactionCount);
    }

    [Fact]
    public async Task Match_MultipleSells_ReturnsCorrectAggregateTotals()
    {
        SeedOrders(Order(101, OrderSide.Buy, quantity: 20),
            Order(102, OrderSide.Sell, quantity: 10, price: 90m, timeOffsetSeconds: 1),
            Order(103, OrderSide.Sell, quantity: 10, price: 95m, timeOffsetSeconds: 2));

        var match = AsMatchResult(await controller.Match(10, CancellationToken.None));

        Assert.Equal(10, match.InstrumentId);
        Assert.Equal(2, match.TotalExecutions);
        Assert.Equal(20, match.TotalExecutedQuantity);
        Assert.Equal(2_000m, match.TotalTradedValue);
        Assert.Equal(2, match.Executions.Count);
    }

    [Fact]
    public async Task Match_SecondCall_DoesNotSettlePreviouslyFilledOrdersAgain()
    {
        SeedOrders(Order(101, OrderSide.Buy), Order(102, OrderSide.Sell, price: 90m, timeOffsetSeconds: 1));
        Assert.Single(MatchExecutions(await controller.Match(10, CancellationToken.None)));

        var second = await controller.Match(10, CancellationToken.None);

        Assert.Empty(MatchExecutions(second));
        Assert.Single(executions.All);
        Assert.Equal(1, uow.TransactionCount);
    }

    [Fact]
    public async Task Match_DifferentInstrument_DoesNotExecuteUnrelatedOrders()
    {
        SeedOrders(Order(101, OrderSide.Buy), Order(102, OrderSide.Sell, price: 90m, timeOffsetSeconds: 1));
        Assert.Empty(MatchExecutions(await controller.Match(11, CancellationToken.None)));
        Assert.Empty(executions.All);
        Assert.Equal(OrderStatus.Pending, orders.Find(101)!.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Match_InvalidInstrumentId_ReturnsBadRequest(int instrumentId)
    {
        var response = await controller.Match(instrumentId, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Empty(executions.All);
        Assert.False(runtime.RequiresRecovery);
    }

    [Fact]
    public async Task Match_RecoveryRequired_ThrowsWithoutSettling()
    {
        SeedOrders(Order(101, OrderSide.Buy), Order(102, OrderSide.Sell, price: 90m, timeOffsetSeconds: 1));
        runtime.RequiresRecovery = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.Match(10, CancellationToken.None));

        Assert.Contains("recovery", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(executions.All);
        Assert.Equal(OrderStatus.Pending, orders.Find(101)!.Status);
        Assert.Equal(OrderStatus.Pending, engine.GetOrderById(102)!.Status);
        Assert.Equal(0, uow.TransactionCount);
    }

    [Fact]
    public async Task Match_AlreadyCancelledToken_DoesNotModifyState()
    {
        SeedOrders(Order(101, OrderSide.Buy), Order(102, OrderSide.Sell, price: 90m, timeOffsetSeconds: 1));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.Match(10, cts.Token));

        Assert.Empty(executions.All);
        Assert.Equal(OrderStatus.Pending, orders.Find(101)!.Status);
        Assert.Equal(OrderStatus.Pending, engine.GetOrderById(101)!.Status);
        Assert.False(runtime.RequiresRecovery);
        Assert.Equal(0, uow.TransactionCount);
    }

    [Fact]
    public async Task Match_SettlementFailure_SetsRecoveryAndLeavesBookUnconfirmed()
    {
        SeedOrders(Order(101, OrderSide.Buy), Order(102, OrderSide.Sell, price: 90m, timeOffsetSeconds: 1));
        executions.AddFailure = new InvalidOperationException("Simulated DB failure");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.Match(10, CancellationToken.None));

        Assert.Contains("Simulated DB failure", ex.Message);
        Assert.True(runtime.RequiresRecovery);
        Assert.Equal(OrderStatus.Pending, engine.GetOrderById(101)!.Status);
        Assert.Empty(executions.All);
        // FakeUnitOfWork is NOT a transactional database; only assert book and recovery.
    }

    [Fact]
    public async Task Match_RecoveryGateReleasedAfterFailure()
    {
        SeedOrders(Order(101, OrderSide.Buy), Order(102, OrderSide.Sell, price: 90m, timeOffsetSeconds: 1));
        runtime.RequiresRecovery = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.Match(10, CancellationToken.None));
        Assert.Equal(1, runtime.TradingGate.CurrentCount);
    }

    [Fact]
    public async Task GetExecutionById_Existing_MapsEveryField()
    {
        var saved = NewExecution(21, 101, 102, 12, 98.5m);
        executions.Seed(saved);

        var response = await controller.GetExecutionById(21, CancellationToken.None);
        var dto = Assert.IsType<ExecutionResponseDto>(
            Assert.IsType<OkObjectResult>(response.Result).Value);

        Assert.Equal(saved.ExecutionId, dto.ExecutionId);
        Assert.Equal(saved.BuyOrderId, dto.BuyOrderId);
        Assert.Equal(saved.SellOrderId, dto.SellOrderId);
        Assert.Equal(saved.ExecutedQuantity, dto.ExecutedQuantity);
        Assert.Equal(saved.ExecutedPrice, dto.ExecutedPrice);
        Assert.Equal(saved.ExecutedAt, dto.ExecutedAt);
        Assert.Equal(21, executions.LastGetById);
    }

    [Fact]
    public async Task GetExecutionById_Missing_ReturnsNotFound()
    {
        var response = await controller.GetExecutionById(999, CancellationToken.None);
        var notFound = Assert.IsType<NotFoundObjectResult>(response.Result);
        Assert.Contains("999", Assert.IsType<string>(notFound.Value));
    }

    [Fact]
    public async Task GetExecutionById_RepositoryFailure_Propagates()
    {
        executions.ReadFailure = new InvalidOperationException("read failed");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.GetExecutionById(21, CancellationToken.None));
        Assert.Equal("read failed", ex.Message);
    }

    [Fact]
    public async Task GetExecutionsByOrder_ReturnsBuyAndSellSideExecutions()
    {
        executions.Seed(NewExecution(1, 101, 201, 4, 99m));
        executions.Seed(NewExecution(2, 202, 101, 6, 98m));
        executions.Seed(NewExecution(3, 303, 404, 8, 97m));

        var response = await controller.GetExecutionsByOrder(101, CancellationToken.None);
        var list = AsExecutionList(response);

        Assert.Equal(2, list.Count);
        Assert.Equal(new[] { 1, 2 }, list.Select(x => x.ExecutionId).OrderBy(x => x));
        Assert.Equal(101, executions.LastGetByOrderId);
    }

    [Fact]
    public async Task GetExecutionsByOrder_NoMatches_ReturnsEmptyList()
    {
        var response = await controller.GetExecutionsByOrder(999, CancellationToken.None);
        Assert.Empty(AsExecutionList(response));
    }

    [Fact]
    public async Task GetExecutionsByOrder_RepositoryFailure_Propagates()
    {
        executions.ReadFailure = new InvalidOperationException("list failed");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.GetExecutionsByOrder(101, CancellationToken.None));
        Assert.Equal("list failed", ex.Message);
    }

    [Fact]
    public async Task GetExecutionsByOrder_ForwardsCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        await controller.GetExecutionsByOrder(101, cts.Token);
        Assert.Equal(cts.Token, executions.LastReadToken);
    }

    // Important: This test WILL FAIL until GetExecutionById passes ct to GetByIdAsync.
    [Fact]
    public async Task GetExecutionById_ForwardsCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        await controller.GetExecutionById(21, cts.Token);
        Assert.Equal(cts.Token, executions.LastReadToken);
    }

    private static Execution NewExecution(int id, int buy, int sell, int quantity, decimal price) => new()
    {
        ExecutionId = id,
        BuyOrderId = buy,
        SellOrderId = sell,
        ExecutedQuantity = quantity,
        ExecutedPrice = price,
        ExecutedAt = new DateTime(2026, 11, 7, 12, 30, 0, DateTimeKind.Utc)
    };

    private sealed class ExecutionRepo : IExecutionRepository
    {
        private readonly List<Execution> data = new();
        private int nextId = 0;
        public IReadOnlyList<Execution> All => data;
        public Exception? AddFailure { get; set; }
        public Exception? ReadFailure { get; set; }
        public int LastGetById { get; private set; }
        public int LastGetByOrderId { get; private set; }
        public CancellationToken LastReadToken { get; private set; }
        public void Seed(Execution e) { data.Add(e); nextId = Math.Max(nextId, e.ExecutionId); }
        public Task AddAsync(Execution execution, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (AddFailure is not null) throw AddFailure;
            execution.ExecutionId = ++nextId;
            data.Add(execution);
            return Task.CompletedTask;
        }
        public Task<Execution?> GetByIdAsync(int executionId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (ReadFailure is not null) throw ReadFailure;
            LastGetById = executionId; LastReadToken = ct;
            return Task.FromResult(data.FirstOrDefault(e => e.ExecutionId == executionId));
        }
        public Task<List<Execution>> GetByOrderIdAsync(int orderId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (ReadFailure is not null) throw ReadFailure;
            LastGetByOrderId = orderId; LastReadToken = ct;
            return Task.FromResult(data.Where(e => e.BuyOrderId == orderId || e.SellOrderId == orderId).ToList());
        }
    }

    private sealed class OrderRepo : IOrderRepository
    {
        private readonly List<Order> data = new();
        public void Seed(Order order) => data.Add(order);
        public Order? Find(int id) => data.SingleOrDefault(x => x.OrderId == id);
        public Task AddAsync(Order order, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); data.Add(order); return Task.CompletedTask;
        }
        public Task<Order?> GetByIdAsync(int orderId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); return Task.FromResult(Find(orderId));
        }
        public Task<List<Order>> GetByAccountIdAsync(int accountId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); return Task.FromResult(data.Where(x => x.AccountId == accountId).ToList());
        }
        public Task<List<Order>> GetOpenOrdersByInstrumentAsync(int instrumentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(data.Where(x => x.InstrumentId == instrumentId &&
                (x.Status == OrderStatus.Pending || x.Status == OrderStatus.PartiallyFilled)).ToList());
        }
        public Task<List<Order>> GetAllOpenOrdersAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(data.Where(x => x.Status == OrderStatus.Pending || x.Status == OrderStatus.PartiallyFilled).ToList());
        }
    }

    private sealed class AccountRepo : IAccountRepository
    {
        private readonly Dictionary<int, Account> data = new();
        public void Seed(Account a) => data[a.AccountId] = a;
        public Account? Find(int id) => data.GetValueOrDefault(id);
        public Task<Account?> GetByIdAsync(int accountId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Find(accountId));
        }
        public Task AddAsync(Account account, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); data[account.AccountId] = account;
            return Task.CompletedTask;
        }
    }

    private sealed class PositionRepo : IPositionRepository
    {
        private readonly List<Position> data = new();
        public void Seed(Position p) => data.Add(p);
        public Position? Find(int accountId, int instrumentId) =>
            data.SingleOrDefault(x => x.AccountId == accountId && x.InstrumentId == instrumentId);
        public Task<Position?> GetByAccountAndInstrumentAsync(int accountId, int instrumentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); return Task.FromResult(Find(accountId, instrumentId));
        }
        public Task AddAsync(Position position, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); data.Add(position); return Task.CompletedTask;
        }
        public Task<List<Position>> GetByAccountIdAsync(int accountId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); return Task.FromResult(data.Where(x => x.AccountId == accountId).ToList());
        }
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int TransactionCount { get; private set; }
        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); return Task.FromResult(1);
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
