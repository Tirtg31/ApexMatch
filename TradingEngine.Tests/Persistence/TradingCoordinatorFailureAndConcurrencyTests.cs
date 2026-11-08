using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Services;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradingEngine.Tests.Persistence;

// Run only against a disposable Oracle test schema. Each test seeds independent data.
// This suite supplements (does not replace) TradingCoordinatorMatchingIntegrationTests.
public sealed class TradingCoordinatorFailureAndConcurrencyTests
{
    private static TradingDbContext CreateDb(SaveChangesInterceptor? interceptor = null)
    {
        var connection = "User Id=trading;Password=trade_123;Data Source=localhost:1521/XEPDB1";
        var builder = new DbContextOptionsBuilder<TradingDbContext>()
            .UseOracle(connection, o => o.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion21));
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return new TradingDbContext(builder.Options);
    }

    private static TradingCoordinator Coordinator(TradingDbContext db, OrderMatchingEngine engine)
    {
        var uow = new EfUnitOfWork(db, NullLogger<EfUnitOfWork>.Instance);
        var accounts = new AccountRepository(db);
        var positions = new PositionRepository(db);
        var orders = new OrderRepository(db);
        var executions = new ExecutionRepository(db);
        var accountSvc = new AccountService(accounts, uow);
        var positionSvc = new PositionService(positions, uow);
        var settlement = new SettlementService(accountSvc, positionSvc, uow, orders, executions);
        var orderSvc = new OrderService(accountSvc, engine, positionSvc, accounts, uow, orders);
        return new TradingCoordinator(settlement, engine, orderSvc, orders);
    }

    private sealed record Fixture(int BuyerId, int SellerId, int InstrumentId);

    private static async Task<Fixture> SeedAsync()
    {
        await using var db = CreateDb();
        var buyer = new Account
        {
            AccountName = $"FailBuyer_{Guid.NewGuid():N}",
            CashBalance = 20_000m,
            AvailableCash = 20_000m,
            ReservedCash = 0m,
            IsActive = true
        };
        var seller = new Account
        {
            AccountName = $"FailSeller_{Guid.NewGuid():N}",
            CashBalance = 200m,
            AvailableCash = 200m,
            ReservedCash = 0m,
            IsActive = true
        };
        var instrument = new TradeInstrument
        {
            InstrumentName = $"Fail_{Guid.NewGuid():N}",
            InstrumentSymbol = $"F{Guid.NewGuid():N}"[..20],
            IsActive = true
        };
        db.Accounts.AddRange(buyer, seller);
        db.TradeInstruments.Add(instrument);
        await db.SaveChangesAsync();
        db.Positions.Add(new Position
        {
            AccountId = seller.AccountId,
            InstrumentId = instrument.InstrumentId,
            Quantity = 100,
            ReservedQuantity = 0,
            AverageCost = 70m
        });
        await db.SaveChangesAsync();
        return new Fixture(buyer.AccountId, seller.AccountId, instrument.InstrumentId);
    }

    private static Order Buy(Fixture f, int qty = 10) => new()
    {
        AccountId = f.BuyerId,
        InstrumentId = f.InstrumentId,
        Side = OrderSide.Buy,
        OrderType = OrderType.Limit,
        Quantity = qty,
        LimitPrice = 105m
    };
    private static Order Sell(Fixture f, int qty = 10) => new()
    {
        AccountId = f.SellerId,
        InstrumentId = f.InstrumentId,
        Side = OrderSide.Sell,
        OrderType = OrderType.Limit,
        Quantity = qty,
        LimitPrice = 100m
    };

    private static async Task<(int buyId, int sellId)> PlaceAsync(
        TradingCoordinator coordinator, Fixture f, int buyQty = 10, int sellQty = 10)
    {
        // SELL first so its price determines execution price.
        var sell = await coordinator.PlaceSellOrderAsync(Sell(f, sellQty));
        var buy = await coordinator.PlaceBuyOrderAsync(Buy(f, buyQty));
        return (buy.OrderId, sell.OrderId);
    }

    private static async Task CleanupAsync(Fixture f)
    {
        await using var db = CreateDb();
        var ids = await db.Orders.Where(o => o.InstrumentId == f.InstrumentId)
            .Select(o => o.OrderId).ToListAsync();
        await db.Executions.Where(e => ids.Contains(e.BuyOrderId) || ids.Contains(e.SellOrderId))
            .ExecuteDeleteAsync();
        await db.Orders.Where(o => o.InstrumentId == f.InstrumentId).ExecuteDeleteAsync();
        await db.Positions.Where(p => p.InstrumentId == f.InstrumentId).ExecuteDeleteAsync();
        await db.Accounts.Where(a => a.AccountId == f.BuyerId || a.AccountId == f.SellerId).ExecuteDeleteAsync();
        await db.TradeInstruments.Where(i => i.InstrumentId == f.InstrumentId).ExecuteDeleteAsync();
    }

    private static async Task<int> ExecutionCountAsync(TradingDbContext db, int buyId, int sellId) =>
        await db.Executions.AsNoTracking().CountAsync(e => e.BuyOrderId == buyId && e.SellOrderId == sellId);

    // Injection is armed *after* placement, and disarmed automatically after one firing.
    private sealed class FailOnSettlementSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Armed = false;
                throw new InjectedSaveFailureException();
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class InjectedSaveFailureException : Exception { }

    // SavedChanges occurs after SQL updates and before EfUnitOfWork.CommitAsync.
    // Simulating a broken in-memory book here causes ConfirmMatch() to fail AFTER commit.
    // This deliberately models book desynchronization; it does not emulate a network failure.
    private sealed class InvalidateBookAfterSave : SaveChangesInterceptor
    {
        private readonly OrderMatchingEngine engine;
        public bool Armed { get; set; }
        public InvalidateBookAfterSave(OrderMatchingEngine engine) => this.engine = engine;

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Armed = false;
                engine.RebuildFromPersistedOrders(Array.Empty<Order>());
            }
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task SaveFailure_RollsBackEveryEntity_ThenRecoveryAllowsExactlyOneTrade()
    {
        var f = await SeedAsync();
        try
        {
            var injector = new FailOnSettlementSave();
            await using var db = CreateDb(injector);
            var engine = new OrderMatchingEngine();
            var coordinator = Coordinator(db, engine);
            var (buyId, sellId) = await PlaceAsync(coordinator, f);
            injector.Armed = true;
            await Assert.ThrowsAsync<InjectedSaveFailureException>(
                () => coordinator.MatchAndSettleAsync(f.InstrumentId));

            await using (var verify = CreateDb())
            {
                Assert.Equal(0, await ExecutionCountAsync(verify, buyId, sellId));
                var buy = await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderId == buyId);
                var sell = await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderId == sellId);
                var buyer = await verify.Accounts.AsNoTracking().SingleAsync(x => x.AccountId == f.BuyerId);
                var seller = await verify.Accounts.AsNoTracking().SingleAsync(x => x.AccountId == f.SellerId);
                var sellerPos = await verify.Positions.AsNoTracking().SingleAsync(x => x.AccountId == f.SellerId && x.InstrumentId == f.InstrumentId);
                Assert.Equal(0, buy.FilledQuantity);
                Assert.Equal(0, sell.FilledQuantity);
                Assert.Equal(OrderStatus.Pending, buy.Status);
                Assert.Equal(OrderStatus.Pending, sell.Status);
                Assert.Equal(20_000m, buyer.CashBalance);
                Assert.Equal(18_950m, buyer.AvailableCash);
                Assert.Equal(1_050m, buyer.ReservedCash);
                Assert.Equal(200m, seller.CashBalance);
                Assert.Equal(100, sellerPos.Quantity);
                Assert.Equal(10, sellerPos.ReservedQuantity);
                Assert.False(await verify.Positions.AnyAsync(x => x.AccountId == f.BuyerId && x.InstrumentId == f.InstrumentId));
            }
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.MatchAndSettleAsync(f.InstrumentId));
            await coordinator.RecoverOrderBookAsync();
            // New DbContext avoids cached/tracked state after injected failure.
            await using var nextDb = CreateDb();
            var next = Coordinator(nextDb, engine);
            Assert.Single(await next.MatchAndSettleAsync(f.InstrumentId));
            Assert.Empty(await next.MatchAndSettleAsync(f.InstrumentId));
            await using var final = CreateDb();
            Assert.Equal(1, await ExecutionCountAsync(final, buyId, sellId));
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task CommitSucceeds_BookConfirmationFails_RecoveryPreventsReplay()
    {
        var f = await SeedAsync();
        try
        {
            var engine = new OrderMatchingEngine();
            var injector = new InvalidateBookAfterSave(engine);
            await using var db = CreateDb(injector);
            var coordinator = Coordinator(db, engine);
            var (buyId, sellId) = await PlaceAsync(coordinator, f);
            injector.Armed = true;
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.MatchAndSettleAsync(f.InstrumentId));
            Assert.Contains("synchronization failed", failure.Message);

            await using (var verify = CreateDb())
            {
                Assert.Equal(1, await ExecutionCountAsync(verify, buyId, sellId));
                Assert.Equal(OrderStatus.Filled,
                    (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderId == buyId)).Status);
                Assert.Equal(OrderStatus.Filled,
                    (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderId == sellId)).Status);
                Assert.Equal(19_000m,
                    (await verify.Accounts.AsNoTracking().SingleAsync(x => x.AccountId == f.BuyerId)).CashBalance);
                Assert.Equal(10,
                    (await verify.Positions.AsNoTracking().SingleAsync(x => x.AccountId == f.BuyerId && x.InstrumentId == f.InstrumentId)).Quantity);
            }
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.MatchAndSettleAsync(f.InstrumentId));
            await coordinator.RecoverOrderBookAsync();
            Assert.Null(engine.ProposeMatch(f.InstrumentId));
            Assert.Empty(await coordinator.MatchAndSettleAsync(f.InstrumentId));
            await using var final = CreateDb();
            Assert.Equal(1, await ExecutionCountAsync(final, buyId, sellId));
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task TwoIndependentCoordinators_StaleSecondBook_IsRejectedThenRecovered()
    {
        var f = await SeedAsync();
        try
        {
            await using var dbA = CreateDb();
            await using var dbB = CreateDb();
            var engineA = new OrderMatchingEngine();
            var engineB = new OrderMatchingEngine();
            var a = Coordinator(dbA, engineA);
            var b = Coordinator(dbB, engineB);
            var (buyId, sellId) = await PlaceAsync(a, f);
            await b.RecoverOrderBookAsync(); // B now has the same pending orders.
            Assert.NotNull(engineB.ProposeMatch(f.InstrumentId));
            Assert.Single(await a.MatchAndSettleAsync(f.InstrumentId));

            // B's in-memory book is stale; the persisted orders are already Filled.
            await Assert.ThrowsAnyAsync<Exception>(() => b.MatchAndSettleAsync(f.InstrumentId));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => b.MatchAndSettleAsync(f.InstrumentId));
            await b.RecoverOrderBookAsync();
            Assert.Empty(await b.MatchAndSettleAsync(f.InstrumentId));
            await using var verify = CreateDb();
            Assert.Equal(1, await ExecutionCountAsync(verify, buyId, sellId));
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task PartialTrade_RecoveryOnlyMatchesOutstandingQuantity()
    {
        var f = await SeedAsync();
        try
        {
            await using var db = CreateDb();
            var engine = new OrderMatchingEngine();
            var coordinator = Coordinator(db, engine);
            var (buyId, sellId) = await PlaceAsync(coordinator, f, buyQty: 20, sellQty: 8);
            Assert.Single(await coordinator.MatchAndSettleAsync(f.InstrumentId));
            await coordinator.RecoverOrderBookAsync();
            Assert.Null(engine.ProposeMatch(f.InstrumentId)); // No open SELL yet.
            var secondSell = await coordinator.PlaceSellOrderAsync(Sell(f, 12));
            var second = Assert.Single(await coordinator.MatchAndSettleAsync(f.InstrumentId));
            Assert.Equal(12, second.ExecutedQuantity);
            await using var verify = CreateDb();
            Assert.Equal(2, await verify.Executions.CountAsync(e => e.BuyOrderId == buyId));
            var buy = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == buyId);
            Assert.Equal(20, buy.FilledQuantity);
            Assert.Equal(OrderStatus.Filled, buy.Status);
            Assert.Equal(OrderStatus.Filled,
                (await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == secondSell.OrderId)).Status);
            Assert.Equal(OrderStatus.Filled,
                (await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == sellId)).Status);
            Assert.Equal(0m,
                (await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.BuyerId)).ReservedCash);
        }
        finally { await CleanupAsync(f); }
    }
}
