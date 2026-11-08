using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Enums;
using TradingEngine.Core.Services;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradingEngine.Tests.Persistence;

// Requires a disposable Oracle TEST schema with all migrations (including Order.Version) applied.
// These tests intentionally verify persisted state using a NEW DbContext.
public class SettlementServiceIntegrationTests
{
    // Set TRADING_TEST_DB to your test-schema connection string.
    // Do not point this at production: each test inserts and deletes database rows.
    private static TradingDbContext CreateDb()
    {
        string connection = "User Id=trading;Password=trade_123;Data Source=localhost:1521/XEPDB1";
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseOracle(connection, o => o.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion21))
            .Options;
        return new TradingDbContext(options);
    }

    private static SettlementService CreateService(TradingDbContext db)
    {
        var uow = new EfUnitOfWork(db, NullLogger<EfUnitOfWork>.Instance);
        var accounts = new AccountRepository(db);
        var positions = new PositionRepository(db);
        var orders = new OrderRepository(db);
        var executions = new ExecutionRepository(db);

        // Uses the service-based constructor discussed in our latest design.
        return new SettlementService(
            new AccountService(accounts, uow),
            new PositionService(positions, uow),
            uow, orders, executions);
    }

    private sealed record Fixture(int BuyerId, int SellerId, int InstrumentId,
        int BuyOrderId, int SellOrderId);

    private static async Task<Fixture> SeedAsync(
        int buyQuantity = 10, int sellQuantity = 10,
        decimal buyPrice = 105m, decimal sellPrice = 100m,
        decimal buyerCash = 5000m, decimal sellerCash = 100m,
        int sellerShares = 50, int buyerShares = 0,
        decimal buyerAverageCost = 80m, bool buyerActive = true)
    {
        if (buyQuantity <= 0 || sellQuantity <= 0 || sellerShares < sellQuantity)
            throw new ArgumentException("Invalid fixture quantities.");

        await using var db = CreateDb();
        var buyer = new Account
        {
            AccountName = $"Buyer_{Guid.NewGuid():N}",
            CashBalance = buyerCash,
            ReservedCash = buyQuantity * buyPrice,
            AvailableCash = buyerCash - buyQuantity * buyPrice,
            IsActive = buyerActive
        };
        var seller = new Account
        {
            AccountName = $"Seller_{Guid.NewGuid():N}",
            CashBalance = sellerCash,
            ReservedCash = 0m,
            AvailableCash = sellerCash,
            IsActive = true
        };
        var instrument = new TradeInstrument
        {
            InstrumentName = $"Settlement_{Guid.NewGuid():N}",
            InstrumentSymbol = $"T{Guid.NewGuid():N}"[..20],
            IsActive = true
        };
        db.Accounts.AddRange(buyer, seller);
        db.TradeInstruments.Add(instrument);
        await db.SaveChangesAsync();

        db.Positions.Add(new Position
        {
            AccountId = seller.AccountId,
            InstrumentId = instrument.InstrumentId,
            Quantity = sellerShares,
            ReservedQuantity = sellQuantity,
            AverageCost = 75m
        });
        if (buyerShares > 0)
        {
            db.Positions.Add(new Position
            {
                AccountId = buyer.AccountId,
                InstrumentId = instrument.InstrumentId,
                Quantity = buyerShares,
                ReservedQuantity = 0,
                AverageCost = buyerAverageCost
            });
        }
        var now = DateTime.UtcNow;
        var buy = new Order
        {
            AccountId = buyer.AccountId,
            InstrumentId = instrument.InstrumentId,
            Side = OrderSide.Buy,
            OrderType = OrderType.Limit,
            Quantity = buyQuantity,
            LimitPrice = buyPrice,
            FilledQuantity = 0,
            Status = OrderStatus.Pending,
            CreatedAt = now.AddSeconds(1) // SELL is older, so execution price = sellPrice.
        };
        var sell = new Order
        {
            AccountId = seller.AccountId,
            InstrumentId = instrument.InstrumentId,
            Side = OrderSide.Sell,
            OrderType = OrderType.Limit,
            Quantity = sellQuantity,
            LimitPrice = sellPrice,
            FilledQuantity = 0,
            Status = OrderStatus.Pending,
            CreatedAt = now
        };
        db.Orders.AddRange(buy, sell);
        await db.SaveChangesAsync();
        return new Fixture(buyer.AccountId, seller.AccountId, instrument.InstrumentId,
            buy.OrderId, sell.OrderId);
    }

    private static ProposedMatch Proposal(Fixture f, int quantity, decimal price = 100m) =>
        new(f.BuyOrderId, f.SellOrderId, f.InstrumentId, quantity, price);

    private static async Task CleanupAsync(Fixture f)
    {
        await using var db = CreateDb();
        await db.Executions.Where(e => e.BuyOrderId == f.BuyOrderId || e.SellOrderId == f.SellOrderId)
            .ExecuteDeleteAsync();
        await db.Orders.Where(o => o.OrderId == f.BuyOrderId || o.OrderId == f.SellOrderId)
            .ExecuteDeleteAsync();
        await db.Positions.Where(p => p.AccountId == f.BuyerId || p.AccountId == f.SellerId)
            .ExecuteDeleteAsync();
        await db.Accounts.Where(a => a.AccountId == f.BuyerId || a.AccountId == f.SellerId)
            .ExecuteDeleteAsync();
        await db.TradeInstruments.Where(i => i.InstrumentId == f.InstrumentId)
            .ExecuteDeleteAsync();
    }

    private static async Task AssertNotSettledAsync(Fixture f, decimal buyerCash = 5000m,
        decimal expectedReserved = 1050m, int sellerShares = 50, int reservedShares = 10)
    {
        await using var verify = CreateDb();
        var buyer = await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.BuyerId);
        var seller = await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.SellerId);
        var sellPosition = await verify.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.SellerId && p.InstrumentId == f.InstrumentId);
        var buy = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == f.BuyOrderId);
        var sell = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == f.SellOrderId);
        Assert.Equal(buyerCash, buyer.CashBalance);
        Assert.Equal(expectedReserved, buyer.ReservedCash);
        Assert.Equal(buyerCash - expectedReserved, buyer.AvailableCash);
        Assert.Equal(100m, seller.CashBalance);
        Assert.Equal(sellerShares, sellPosition.Quantity);
        Assert.Equal(reservedShares, sellPosition.ReservedQuantity);
        Assert.Equal(0, buy.FilledQuantity);
        Assert.Equal(0, sell.FilledQuantity);
        Assert.Equal(OrderStatus.Pending, buy.Status);
        Assert.Equal(OrderStatus.Pending, sell.Status);
        Assert.Equal(0, await verify.Executions.CountAsync(e => e.BuyOrderId == f.BuyOrderId));
    }

    [Fact]
    public async Task SettleAsync_FullFill_CommitsEveryEntityAndRefundsPriceImprovement()
    {
        var f = await SeedAsync();
        try
        {
            int executionId;
            await using (var db = CreateDb())
                executionId = (await CreateService(db).SettleAsync(Proposal(f, 10))).ExecutionId;

            await using var verify = CreateDb();
            var buyer = await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.BuyerId);
            var seller = await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.SellerId);
            var buyerPosition = await verify.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.BuyerId && p.InstrumentId == f.InstrumentId);
            var sellerPosition = await verify.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.SellerId && p.InstrumentId == f.InstrumentId);
            var buy = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == f.BuyOrderId);
            var sell = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == f.SellOrderId);
            var execution = await verify.Executions.AsNoTracking().SingleAsync(e => e.ExecutionId == executionId);

            Assert.Equal(4000m, buyer.CashBalance);
            Assert.Equal(4000m, buyer.AvailableCash);
            Assert.Equal(0m, buyer.ReservedCash);
            Assert.Equal(1100m, seller.CashBalance);
            Assert.Equal(1100m, seller.AvailableCash);
            Assert.Equal(10, buyerPosition.Quantity);
            Assert.Equal(100m, buyerPosition.AverageCost);
            Assert.Equal(40, sellerPosition.Quantity);
            Assert.Equal(0, sellerPosition.ReservedQuantity);
            Assert.Equal(10, buy.FilledQuantity);
            Assert.Equal(10, sell.FilledQuantity);
            Assert.Equal(OrderStatus.Filled, buy.Status);
            Assert.Equal(OrderStatus.Filled, sell.Status);
            Assert.Equal(10, execution.ExecutedQuantity);
            Assert.Equal(100m, execution.ExecutedPrice);
            Assert.Equal(f.BuyOrderId, execution.BuyOrderId);
            Assert.Equal(f.SellOrderId, execution.SellOrderId);
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task SettleAsync_PartialBuyFill_PreservesRemainingBuyReservation()
    {
        var f = await SeedAsync(buyQuantity: 20, sellQuantity: 8);
        try
        {
            await using (var db = CreateDb())
                await CreateService(db).SettleAsync(Proposal(f, 8));

            await using var verify = CreateDb();
            var buyer = await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.BuyerId);
            var buy = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == f.BuyOrderId);
            var sell = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == f.SellOrderId);
            var pos = await verify.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.SellerId);
            Assert.Equal(4200m, buyer.CashBalance);
            Assert.Equal(1260m, buyer.ReservedCash);
            Assert.Equal(2940m, buyer.AvailableCash);
            Assert.Equal(8, buy.FilledQuantity);
            Assert.Equal(OrderStatus.PartiallyFilled, buy.Status);
            Assert.Equal(8, sell.FilledQuantity);
            Assert.Equal(OrderStatus.Filled, sell.Status);
            Assert.Equal(42, pos.Quantity);
            Assert.Equal(0, pos.ReservedQuantity);
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task SettleAsync_PartialSellFill_PreservesRemainingReservedShares()
    {
        var f = await SeedAsync(buyQuantity: 6, sellQuantity: 15);
        try
        {
            await using (var db = CreateDb())
                await CreateService(db).SettleAsync(Proposal(f, 6));

            await using var verify = CreateDb();
            var sell = await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == f.SellOrderId);
            var pos = await verify.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.SellerId);
            Assert.Equal(6, sell.FilledQuantity);
            Assert.Equal(OrderStatus.PartiallyFilled, sell.Status);
            Assert.Equal(44, pos.Quantity);
            Assert.Equal(9, pos.ReservedQuantity);
            Assert.Equal(35, pos.AvailableQuantity);
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task SettleAsync_ExistingBuyerPosition_RecalculatesWeightedAverageCost()
    {
        var f = await SeedAsync(buyerShares: 10, buyerAverageCost: 80m);
        try
        {
            await using (var db = CreateDb())
                await CreateService(db).SettleAsync(Proposal(f, 10));
            await using var verify = CreateDb();
            var pos = await verify.Positions.AsNoTracking().SingleAsync(p => p.AccountId == f.BuyerId);
            Assert.Equal(20, pos.Quantity);
            Assert.Equal(90m, pos.AverageCost);
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task SettleAsync_DuplicateProposal_DoesNotSettleTwice()
    {
        var f = await SeedAsync();
        try
        {
            await using (var first = CreateDb())
                await CreateService(first).SettleAsync(Proposal(f, 10));
            await using (var second = CreateDb())
                await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(second).SettleAsync(Proposal(f, 10)));
            await using var verify = CreateDb();
            Assert.Equal(1, await verify.Executions.CountAsync(e => e.BuyOrderId == f.BuyOrderId));
            Assert.Equal(4000m, (await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == f.BuyerId)).CashBalance);
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task SettleAsync_InsufficientBuyerReservation_RollsBackWithoutWritingExecution()
    {
        var f = await SeedAsync();
        try
        {
            await using (var corrupt = CreateDb())
            {
                var buyer = await corrupt.Accounts.SingleAsync(a => a.AccountId == f.BuyerId);
                buyer.ReservedCash = 100m;
                buyer.AvailableCash = buyer.CashBalance - 100m;
                await corrupt.SaveChangesAsync();
            }
            await using (var db = CreateDb())
                await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(db).SettleAsync(Proposal(f, 10)));
            await AssertNotSettledAsync(f, expectedReserved: 100m);
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task SettleAsync_InsufficientSellerReservedShares_RollsBackBuyerUpdates()
    {
        var f = await SeedAsync();
        try
        {
            await using (var corrupt = CreateDb())
            {
                var pos = await corrupt.Positions.SingleAsync(p => p.AccountId == f.SellerId);
                pos.ReservedQuantity = 4;
                await corrupt.SaveChangesAsync();
            }
            await using (var db = CreateDb())
                await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(db).SettleAsync(Proposal(f, 10)));
            await AssertNotSettledAsync(f, reservedShares: 4);
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task SettleAsync_InvalidPrice_DoesNotChangePersistedState()
    {
        var f = await SeedAsync();
        try
        {
            await using (var db = CreateDb())
                await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(db).SettleAsync(Proposal(f, 10, 104m)));
            await AssertNotSettledAsync(f);
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task SettleAsync_InactiveBuyer_DoesNotChangePersistedState()
    {
        var f = await SeedAsync(buyerActive: false);
        try
        {
            await using (var db = CreateDb())
                await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(db).SettleAsync(Proposal(f, 10)));
            await AssertNotSettledAsync(f);
        }
        finally { await CleanupAsync(f); }
    }

    [Fact]
    public async Task SettleAsync_InvalidQuantity_DoesNotChangePersistedState()
    {
        var f = await SeedAsync();
        try
        {
            await using (var db = CreateDb())
                await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(db).SettleAsync(Proposal(f, 9)));
            await AssertNotSettledAsync(f);
        }
        finally { await CleanupAsync(f); }
    }
}
