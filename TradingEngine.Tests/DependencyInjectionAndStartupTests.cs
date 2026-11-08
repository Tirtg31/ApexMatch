using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Interfaces.Repositories;
using TradingEngine.Core.Services;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;

namespace TradingEngine.Tests;

/// <summary>
/// DI and recovery tests. No Oracle server is needed: a valid Oracle provider configuration
/// is registered, but the only repository operation executed is on FakeOrderRepository.
/// This test composition mirrors Program.cs; it does not automatically verify Program.cs.
/// </summary>
public sealed class DependencyInjectionAndStartupTests
{
    private static ServiceProvider BuildProvider(FakeOrderRepository? fakeOrders = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(); // Required by EfUnitOfWork's ILogger<EfUnitOfWork>.
        services.AddDbContext<TradingDbContext>(options =>
            options.UseOracle(
                "User Id=test;Password=test;Data Source=localhost:1521/XEPDB1",
                oracle => oracle.UseOracleSQLCompatibility(
                    OracleSQLCompatibility.DatabaseVersion21)));

        services.AddSingleton<TradingRuntimeState>();
        services.AddSingleton<OrderMatchingEngine>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IPositionRepository, PositionRepository>();
        services.AddScoped<IExecutionRepository, ExecutionRepository>();
        if (fakeOrders is null)
            services.AddScoped<IOrderRepository, OrderRepository>();
        else
            services.AddScoped<IOrderRepository>(_ => fakeOrders);
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<AccountService>();
        services.AddScoped<PositionService>();
        services.AddScoped<OrderService>();
        services.AddScoped<SettlementService>();
        services.AddScoped<TradingCoordinator>(sp => new TradingCoordinator(
            sp.GetRequiredService<SettlementService>(),
            sp.GetRequiredService<OrderMatchingEngine>(),
            sp.GetRequiredService<OrderService>(),
            sp.GetRequiredService<IOrderRepository>(),
            sp.GetRequiredService<TradingRuntimeState>()));

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    [Fact]
    public void MatchingEngine_IsSameSingletonAcrossScopes()
    {
        using var provider = BuildProvider();
        using var a = provider.CreateScope();
        using var b = provider.CreateScope();

        var first = a.ServiceProvider.GetRequiredService<OrderMatchingEngine>();
        var second = b.ServiceProvider.GetRequiredService<OrderMatchingEngine>();

        Assert.Same(first, second);
    }

    [Fact]
    public void RuntimeState_IsSameSingletonAcrossScopes_AndStartsSuspended()
    {
        using var provider = BuildProvider();
        using var a = provider.CreateScope();
        using var b = provider.CreateScope();

        var first = a.ServiceProvider.GetRequiredService<TradingRuntimeState>();
        var second = b.ServiceProvider.GetRequiredService<TradingRuntimeState>();

        Assert.Same(first, second);
        Assert.True(first.RequiresRecovery);
        Assert.Same(first.TradingGate, second.TradingGate);
    }

    [Fact]
    public void DbContext_IsSameWithinScope_ButDifferentAcrossScopes()
    {
        using var provider = BuildProvider();
        using var a = provider.CreateScope();
        using var b = provider.CreateScope();

        var a1 = a.ServiceProvider.GetRequiredService<TradingDbContext>();
        var a2 = a.ServiceProvider.GetRequiredService<TradingDbContext>();
        var b1 = b.ServiceProvider.GetRequiredService<TradingDbContext>();

        Assert.Same(a1, a2);
        Assert.NotSame(a1, b1);
    }

    [Fact]
    public void RepositoriesAndUnitOfWork_AreScoped()
    {
        using var provider = BuildProvider();
        using var a = provider.CreateScope();
        using var b = provider.CreateScope();

        Assert.Same(
            a.ServiceProvider.GetRequiredService<IOrderRepository>(),
            a.ServiceProvider.GetRequiredService<IOrderRepository>());
        Assert.NotSame(
            a.ServiceProvider.GetRequiredService<IOrderRepository>(),
            b.ServiceProvider.GetRequiredService<IOrderRepository>());
        Assert.Same(
            a.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            a.ServiceProvider.GetRequiredService<IUnitOfWork>());
        Assert.NotSame(
            a.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            b.ServiceProvider.GetRequiredService<IUnitOfWork>());
    }

    [Fact]
    public void AllApiServices_ResolveFromScope()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        Assert.NotNull(sp.GetRequiredService<AccountService>());
        Assert.NotNull(sp.GetRequiredService<PositionService>());
        Assert.NotNull(sp.GetRequiredService<OrderService>());
        Assert.NotNull(sp.GetRequiredService<SettlementService>());
        Assert.NotNull(sp.GetRequiredService<TradingCoordinator>());
    }

    [Fact]
    public void Coordinator_IsScoped_WhileItsRuntimeAndEngineAreShared()
    {
        using var provider = BuildProvider();
        using var a = provider.CreateScope();
        using var b = provider.CreateScope();

        var first = a.ServiceProvider.GetRequiredService<TradingCoordinator>();
        var again = a.ServiceProvider.GetRequiredService<TradingCoordinator>();
        var second = b.ServiceProvider.GetRequiredService<TradingCoordinator>();

        Assert.Same(first, again);
        Assert.NotSame(first, second);
        // The shared state is verified behaviorally by the gate contention test below.
        Assert.Same(
            a.ServiceProvider.GetRequiredService<TradingRuntimeState>(),
            b.ServiceProvider.GetRequiredService<TradingRuntimeState>());
    }

    [Fact]
    public async Task TwoScopedCoordinators_CannotPassTheSharedGateSimultaneously()
    {
        using var provider = BuildProvider();
        using var a = provider.CreateScope();
        using var b = provider.CreateScope();
        var state = provider.GetRequiredService<TradingRuntimeState>();
        state.RequiresRecovery = false;
        var first = a.ServiceProvider.GetRequiredService<TradingCoordinator>();
        var second = b.ServiceProvider.GetRequiredService<TradingCoordinator>();

        await state.TradingGate.WaitAsync();
        Task<List<Execution>>? task1 = null;
        Task<List<Execution>>? task2 = null;
        try
        {
            // Neither call can enter until the shared gate is released.
            task1 = first.MatchAndSettleAsync(123);
            task2 = second.MatchAndSettleAsync(123);
            Assert.False(task1.IsCompleted);
            Assert.False(task2.IsCompleted);
        }
        finally
        {
            state.TradingGate.Release();
        }

        Assert.Empty(await task1!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(await task2!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, state.TradingGate.CurrentCount);
    }

    [Fact]
    public async Task Recovery_Success_RebuildsBookAndEnablesTrading()
    {
        var order = NewOpenOrder(801);
        var fake = new FakeOrderRepository { OpenOrders = new() { order } };
        using var provider = BuildProvider(fake);
        using var scope = provider.CreateScope();
        var engine = provider.GetRequiredService<OrderMatchingEngine>();
        var state = provider.GetRequiredService<TradingRuntimeState>();
        var coordinator = scope.ServiceProvider.GetRequiredService<TradingCoordinator>();

        Assert.True(state.RequiresRecovery);
        Assert.Null(engine.GetOrderById(order.OrderId));

        await coordinator.RecoverOrderBookAsync();

        Assert.False(state.RequiresRecovery);
        Assert.Equal(1, fake.RecoveryReads);
        var restored = Assert.IsType<Order>(engine.GetOrderById(order.OrderId));
        Assert.Equal(order.Quantity, restored.Quantity);
        Assert.Equal(order.LimitPrice, restored.LimitPrice);
    }

    [Fact]
    public async Task Recovery_CalledTwice_DoesNotDuplicateOpenOrders()
    {
        var fake = new FakeOrderRepository { OpenOrders = new() { NewOpenOrder(802) } };
        using var provider = BuildProvider(fake);
        using var scope = provider.CreateScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<TradingCoordinator>();
        var state = provider.GetRequiredService<TradingRuntimeState>();
        var engine = provider.GetRequiredService<OrderMatchingEngine>();

        await coordinator.RecoverOrderBookAsync();
        await coordinator.RecoverOrderBookAsync();

        Assert.False(state.RequiresRecovery);
        Assert.Equal(2, fake.RecoveryReads);
        Assert.NotNull(engine.GetOrderById(802));
    }

    [Fact]
    public async Task Recovery_Failure_LeavesTradingSuspended()
    {
        var fake = new FakeOrderRepository { RecoveryException = new InvalidOperationException("Oracle unavailable") };
        using var provider = BuildProvider(fake);
        using var scope = provider.CreateScope();
        var state = provider.GetRequiredService<TradingRuntimeState>();
        var coordinator = scope.ServiceProvider.GetRequiredService<TradingCoordinator>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.RecoverOrderBookAsync());

        Assert.Equal("Oracle unavailable", exception.Message);
        Assert.True(state.RequiresRecovery);
        Assert.Equal(1, state.TradingGate.CurrentCount); // Gate isn't leaked on failure.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.MatchAndSettleAsync(123));
    }

    private static Order NewOpenOrder(int orderId) => new()
    {
        OrderId = orderId,
        AccountId = 1,
        InstrumentId = 123,
        Side = OrderSide.Buy,
        OrderType = OrderType.Limit,
        Quantity = 10,
        FilledQuantity = 0,
        LimitPrice = 100m,
        Status = OrderStatus.Pending,
        CreatedAt = DateTime.UtcNow.AddMinutes(-1)
    };

    private sealed class FakeOrderRepository : IOrderRepository
    {
        public List<Order> OpenOrders { get; set; } = new();
        public Exception? RecoveryException { get; set; }
        public int RecoveryReads { get; private set; }

        public Task<List<Order>> GetAllOpenOrdersAsync(CancellationToken ct = default)
        {
            RecoveryReads++;
            if (RecoveryException is not null)
                throw RecoveryException;
            return Task.FromResult(OpenOrders.ToList());
        }

        public Task AddAsync(Order order, CancellationToken ct = default) =>
            throw new NotSupportedException("Recovery-only fake");
        public Task<Order?> GetByIdAsync(int orderId, CancellationToken ct = default) =>
            throw new NotSupportedException("Recovery-only fake");
        public Task<List<Order>> GetByAccountIdAsync(int accountId, CancellationToken ct = default) =>
            throw new NotSupportedException("Recovery-only fake");
        public Task<List<Order>> GetOpenOrdersByInstrumentAsync(int instrumentId, CancellationToken ct = default) =>
            throw new NotSupportedException("Recovery-only fake");
    }
}
