using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Services;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradingEngine.Tests.Persistence;

// Requires the Position.Version migration and a DEDICATED Oracle test schema.
// Set TRADING_ENGINE_TEST_CONNECTION to the same connection string used by
// your existing repository integration tests before running these tests.
// PositionService must have the (IPositionRepository, IUnitOfWork) constructor
// and six async methods shown in the conversation.
public sealed class PositionServiceAndConcurrencyIntegrationTests : IAsyncLifetime
{
    private readonly List<int> _accounts = new();
    private readonly List<int> _instruments = new();

    private static TradingDbContext CreateDbContext()
    {
        string cs = "User Id=trading;Password=trade_123;Data Source=localhost:1521/XEPDB1";
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseOracle(cs, x => x.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion21))
            .Options;
        return new TradingDbContext(options);
    }

    private static EfUnitOfWork Uow(TradingDbContext db) =>
        new(db, NullLogger<EfUnitOfWork>.Instance);

    private static PositionService Service(TradingDbContext db) =>
        new(new PositionRepository(db), Uow(db));

    private async Task<(int accountId, int instrumentId)> ParentsAsync()
    {
        await using var db = CreateDbContext();
        var account = new Account
        {
            AccountName = $"PosIT_{Guid.NewGuid():N}",
            CashBalance = 10000m,
            AvailableCash = 10000m,
            ReservedCash = 0m,
            IsActive = true
        };
        var instrument = new TradeInstrument
        {
            InstrumentName = $"Position Integration {Guid.NewGuid():N}",
            InstrumentSymbol = $"P{Guid.NewGuid():N}"[..18],
            IsActive = true
        };
        db.Accounts.Add(account);
        db.TradeInstruments.Add(instrument);
        await db.SaveChangesAsync();
        _accounts.Add(account.AccountId);
        _instruments.Add(instrument.InstrumentId);
        return (account.AccountId, instrument.InstrumentId);
    }

    private async Task<(int accountId, int instrumentId)> PositionAsync(
        int qty = 100, decimal cost = 200m)
    {
        var parents = await ParentsAsync();
        await using var db = CreateDbContext();
        await Service(db).AddPositionAsync(parents.accountId, parents.instrumentId, qty, cost);
        return parents;
    }

    private static async Task<Position> ReadAsync(int accountId, int instrumentId)
    {
        await using var db = CreateDbContext();
        return await db.Positions.AsNoTracking().SingleAsync(p =>
            p.AccountId == accountId && p.InstrumentId == instrumentId);
    }

    private static void AssertPosition(Position p, int qty, int reserved, decimal averageCost)
    {
        Assert.Equal(qty, p.Quantity);
        Assert.Equal(reserved, p.ReservedQuantity);
        Assert.Equal(qty - reserved, p.AvailableQuantity);
        Assert.Equal(averageCost, p.AverageCost);
        Assert.InRange(p.ReservedQuantity, 0, p.Quantity);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_accounts.Count == 0) return;
        await using var db = CreateDbContext();
        await db.Positions.Where(p => _accounts.Contains(p.AccountId)).ExecuteDeleteAsync();
        await db.TradeInstruments.Where(t => _instruments.Contains(t.InstrumentId)).ExecuteDeleteAsync();
        await db.Accounts.Where(a => _accounts.Contains(a.AccountId)).ExecuteDeleteAsync();
    }

    // SERVICE BUSINESS RULES AND PERSISTENCE

    [Fact]
    public async Task AddPosition_NewPosition_IsPersistedWithInitialValues()
    {
        var (a, i) = await ParentsAsync();
        await using (var db = CreateDbContext())
            await Service(db).AddPositionAsync(a, i, 100, 200m);
        var saved = await ReadAsync(a, i);
        Assert.True(saved.PositionId > 0);
        AssertPosition(saved, 100, 0, 200m);
        Assert.Equal(1L, saved.Version);
    }

    [Fact]
    public async Task AddPosition_ExistingPosition_UpdatesWeightedAverageAndQuantity()
    {
        var (a, i) = await PositionAsync();
        await using (var db = CreateDbContext())
            await Service(db).AddPositionAsync(a, i, 100, 300m);
        AssertPosition(await ReadAsync(a, i), 200, 0, 250m);
        Assert.Equal(1, await CountPositionsAsync(a, i));
    }

    [Fact]
    public async Task AddPosition_ExistingReservedShares_PreservesReservations()
    {
        var (a, i) = await PositionAsync();
        await using (var db = CreateDbContext())
        {
            await Service(db).ReserveQuantityAsync(a, i, 60);
            await Service(db).AddPositionAsync(a, i, 100, 300m);
        }
        AssertPosition(await ReadAsync(a, i), 200, 60, 250m);
    }

    [Fact]
    public async Task GetPosition_Existing_ReturnsMatchingPosition()
    {
        var (a, i) = await PositionAsync();
        await using var db = CreateDbContext();
        var p = await Service(db).GetPositionAsync(a, i);
        Assert.NotNull(p);
        Assert.Equal(a, p.AccountId);
        Assert.Equal(i, p.InstrumentId);
        AssertPosition(p, 100, 0, 200m);
    }

    [Fact]
    public async Task GetPosition_Missing_ReturnsNull()
    {
        var (a, i) = await ParentsAsync();
        await using var db = CreateDbContext();
        Assert.Null(await Service(db).GetPositionAsync(a, i));
    }

    [Fact]
    public async Task ReserveQuantity_Valid_PersistsReservationWithoutReducingHoldings()
    {
        var (a, i) = await PositionAsync();
        await using (var db = CreateDbContext())
            await Service(db).ReserveQuantityAsync(a, i, 40);
        AssertPosition(await ReadAsync(a, i), 100, 40, 200m);
    }

    [Fact]
    public async Task ReserveQuantity_ExactAvailable_IsAllowed()
    {
        var (a, i) = await PositionAsync();
        await using (var db = CreateDbContext())
            await Service(db).ReserveQuantityAsync(a, i, 100);
        AssertPosition(await ReadAsync(a, i), 100, 100, 200m);
    }

    [Fact]
    public async Task ReserveQuantity_SecondReservationOverAvailable_IsRejectedAndUnchanged()
    {
        var (a, i) = await PositionAsync();
        await using (var db = CreateDbContext())
        {
            await Service(db).ReserveQuantityAsync(a, i, 70);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                Service(db).ReserveQuantityAsync(a, i, 40));
        }
        AssertPosition(await ReadAsync(a, i), 100, 70, 200m);
    }

    [Fact]
    public async Task ReserveQuantity_NoPosition_IsRejected()
    {
        var (a, i) = await ParentsAsync();
        await using var db = CreateDbContext();
        await Assert.ThrowsAsync<ArgumentException>(() => Service(db).ReserveQuantityAsync(a, i, 5));
    }

    [Fact]
    public async Task ReleaseReservedQuantity_PartialRelease_RestoresAvailability()
    {
        var (a, i) = await PositionAsync();
        await using (var db = CreateDbContext())
        {
            await Service(db).ReserveQuantityAsync(a, i, 70);
            await Service(db).ReleaseReservedQuantityAsync(a, i, 25);
        }
        AssertPosition(await ReadAsync(a, i), 100, 45, 200m);
    }

    [Fact]
    public async Task ReleaseReservedQuantity_FullRelease_LeavesZeroReserved()
    {
        var (a, i) = await PositionAsync();
        await using (var db = CreateDbContext())
        {
            await Service(db).ReserveQuantityAsync(a, i, 70);
            await Service(db).ReleaseReservedQuantityAsync(a, i, 70);
        }
        AssertPosition(await ReadAsync(a, i), 100, 0, 200m);
    }

    [Fact]
    public async Task ReleaseReservedQuantity_ExcessiveRelease_IsRejectedAndUnchanged()
    {
        var (a, i) = await PositionAsync();
        await using (var db = CreateDbContext())
        {
            await Service(db).ReserveQuantityAsync(a, i, 20);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Service(db).ReleaseReservedQuantityAsync(a, i, 21));
        }
        AssertPosition(await ReadAsync(a, i), 100, 20, 200m);
    }

    [Fact]
    public async Task ReleaseReservedQuantity_NoPosition_IsRejected()
    {
        var (a, i) = await ParentsAsync();
        await using var db = CreateDbContext();
        await Assert.ThrowsAsync<ArgumentException>(() => Service(db).ReleaseReservedQuantityAsync(a, i, 1));
    }

    [Fact]
    public async Task ReducePosition_UnreservedShares_DecreasesOnlyQuantity()
    {
        var (a, i) = await PositionAsync();
        await using (var db = CreateDbContext())
        {
            await Service(db).ReserveQuantityAsync(a, i, 30);
            await Service(db).ReducePositionAsync(a, i, 50);
        }
        AssertPosition(await ReadAsync(a, i), 50, 30, 200m);
    }

    [Fact]
    public async Task ReducePosition_ExactAvailable_IsAllowed()
    {
        var (a, i) = await PositionAsync();
        await using (var db = CreateDbContext())
        {
            await Service(db).ReserveQuantityAsync(a, i, 30);
            await Service(db).ReducePositionAsync(a, i, 70);
        }
        AssertPosition(await ReadAsync(a, i), 30, 30, 200m);
    }

    [Fact]
    public async Task ReducePosition_MoreThanUnreserved_IsRejectedAndUnchanged()
    {
        var (a, i) = await PositionAsync();
        await using (var db = CreateDbContext())
        {
            await Service(db).ReserveQuantityAsync(a, i, 80);
            await Assert.ThrowsAsync<ArgumentException>(() => Service(db).ReducePositionAsync(a, i, 21));
        }
        AssertPosition(await ReadAsync(a, i), 100, 80, 200m);
    }

    [Fact]
    public async Task ReducePosition_NoPosition_IsRejected()
    {
        var (a, i) = await ParentsAsync();
        await using var db = CreateDbContext();
        await Assert.ThrowsAsync<ArgumentException>(() => Service(db).ReducePositionAsync(a, i, 1));
    }

    [Fact]
    public async Task IncreasePosition_ReusesAddLogic_UpdatesWeightedAverage()
    {
        var (a, i) = await PositionAsync();
        await using (var db = CreateDbContext())
            await Service(db).IncreasePositionAsync(a, i, 100, 300m);
        AssertPosition(await ReadAsync(a, i), 200, 0, 250m);
    }

    [Fact]
    public async Task IncreasePosition_NoExistingPosition_CreatesIt()
    {
        var (a, i) = await ParentsAsync();
        await using (var db = CreateDbContext())
            await Service(db).IncreasePositionAsync(a, i, 20, 50m);
        AssertPosition(await ReadAsync(a, i), 20, 0, 50m);
    }

    [Theory]
    [InlineData(0, 1, 10, 100)]
    [InlineData(-1, 1, 10, 100)]
    [InlineData(1, 0, 10, 100)]
    [InlineData(1, -1, 10, 100)]
    [InlineData(1, 1, 0, 100)]
    [InlineData(1, 1, -1, 100)]
    [InlineData(1, 1, 10, 0)]
    [InlineData(1, 1, 10, -5)]
    public async Task AddPosition_InvalidInputs_Throws(int account, int instrument, int qty, int price)
    {
        await using var db = CreateDbContext();
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            Service(db).AddPositionAsync(account, instrument, qty, price));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public async Task GetPosition_InvalidIds_Throws(int account, int instrument)
    {
        await using var db = CreateDbContext();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Service(db).GetPositionAsync(account, instrument));
    }

    [Theory]
    [InlineData("reserve", 0, 1, 1)]
    [InlineData("reserve", 1, 0, 1)]
    [InlineData("reserve", 1, 1, 0)]
    [InlineData("reserve", 1, 1, -1)]
    [InlineData("release", 0, 1, 1)]
    [InlineData("release", 1, 0, 1)]
    [InlineData("release", 1, 1, 0)]
    [InlineData("release", 1, 1, -1)]
    [InlineData("reduce", 0, 1, 1)]
    [InlineData("reduce", 1, 0, 1)]
    [InlineData("reduce", 1, 1, 0)]
    [InlineData("reduce", 1, 1, -1)]
    public async Task QuantityOperations_InvalidInputs_Throw(
        string operation, int account, int instrument, int qty)
    {
        await using var db = CreateDbContext();
        var service = Service(db);
        Func<Task> action = operation switch
        {
            "reserve" => () => service.ReserveQuantityAsync(account, instrument, qty),
            "release" => () => service.ReleaseReservedQuantityAsync(account, instrument, qty),
            _ => () => service.ReducePositionAsync(account, instrument, qty)
        };
        await Assert.ThrowsAnyAsync<ArgumentException>(action);
    }

    // OPTIMISTIC CONCURRENCY / TRANSACTION TESTS

    [Fact]
    public async Task PositionVersion_Modification_IncrementsButUnchangedSaveDoesNot()
    {
        var (a, i) = await PositionAsync();
        await using (var db = CreateDbContext())
        {
            var p = await db.Positions.SingleAsync(p => p.AccountId == a && p.InstrumentId == i);
            Assert.Equal(1L, p.Version);
            await db.SaveChangesAsync();
            Assert.Equal(1L, p.Version);
            p.ReservedQuantity = 10;
            await db.SaveChangesAsync();
            Assert.Equal(2L, p.Version);
            p.ReservedQuantity = 15;
            await db.SaveChangesAsync();
            Assert.Equal(3L, p.Version);
        }
        Assert.Equal(3L, (await ReadAsync(a, i)).Version);
    }

    [Fact]
    public async Task TwoContexts_StalePositionWrite_ThrowsAndPreservesCommittedData()
    {
        var (a, i) = await PositionAsync();
        await using var db1 = CreateDbContext();
        await using var db2 = CreateDbContext();
        var p1 = await db1.Positions.SingleAsync(p => p.AccountId == a && p.InstrumentId == i);
        var p2 = await db2.Positions.SingleAsync(p => p.AccountId == a && p.InstrumentId == i);
        Assert.Equal(p1.Version, p2.Version);
        p1.ReservedQuantity = 70;
        await db1.SaveChangesAsync();
        p2.ReservedQuantity = 70;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
        var saved = await ReadAsync(a, i);
        AssertPosition(saved, 100, 70, 200m);
        Assert.Equal(2L, saved.Version);
    }

    [Fact]
    public async Task TwoContexts_SeparatePositions_DoNotConflict()
    {
        var (a1, i1) = await PositionAsync();
        var (a2, i2) = await PositionAsync();
        await using var db1 = CreateDbContext();
        await using var db2 = CreateDbContext();
        var p1 = await db1.Positions.SingleAsync(p => p.AccountId == a1 && p.InstrumentId == i1);
        var p2 = await db2.Positions.SingleAsync(p => p.AccountId == a2 && p.InstrumentId == i2);
        p1.ReservedQuantity = 40;
        p2.ReservedQuantity = 60;
        await db1.SaveChangesAsync();
        await db2.SaveChangesAsync();
        AssertPosition(await ReadAsync(a1, i1), 100, 40, 200m);
        AssertPosition(await ReadAsync(a2, i2), 100, 60, 200m);
    }

    [Fact]
    public async Task StaleReservation_ReloadLatestPosition_RejectsExcessiveRequest()
    {
        var (a, i) = await PositionAsync();
        await using var staleDb = CreateDbContext();
        var stale = await staleDb.Positions.SingleAsync(p => p.AccountId == a && p.InstrumentId == i);
        await using (var firstDb = CreateDbContext())
            await Service(firstDb).ReserveQuantityAsync(a, i, 70);
        // The stale snapshot still appears to have 100 available.
        Assert.Equal(100, stale.AvailableQuantity);
        stale.ReservedQuantity += 40;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleDb.SaveChangesAsync());
        // Retry the WHOLE operation against fresh data, not the failed DbContext.
        await using (var retryDb = CreateDbContext())
            await Assert.ThrowsAsync<ArgumentException>(() =>
                Service(retryDb).ReserveQuantityAsync(a, i, 40));
        AssertPosition(await ReadAsync(a, i), 100, 70, 200m);
    }

    [Fact]
    public async Task ConcurrencyConflict_RollsBackOtherPositionChangesInSameUnitOfWork()
    {
        var (a1, i1) = await PositionAsync();
        var (a2, i2) = await PositionAsync();

        await using var staleDb = CreateDbContext();
        var stale1 = await staleDb.Positions.SingleAsync(p => p.AccountId == a1 && p.InstrumentId == i1);
        var stale2 = await staleDb.Positions.SingleAsync(p => p.AccountId == a2 && p.InstrumentId == i2);

        // Make position 2 stale in another committed transaction.
        await using (var freshDb = CreateDbContext())
        {
            var fresh2 = await freshDb.Positions.SingleAsync(p => p.AccountId == a2 && p.InstrumentId == i2);
            fresh2.ReservedQuantity = 20;
            await freshDb.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            Uow(staleDb).ExecuteInTransactionAsync(async ct =>
            {
                stale1.ReservedQuantity = 40;
                stale2.ReservedQuantity = 50;
                await Task.CompletedTask;
                return true;
            }));

        // Even if position 1 was flushed before the stale write, the transaction rolls back.
        AssertPosition(await ReadAsync(a1, i1), 100, 0, 200m);
        Assert.Equal(1L, (await ReadAsync(a1, i1)).Version);
        AssertPosition(await ReadAsync(a2, i2), 100, 20, 200m);
        Assert.Equal(2L, (await ReadAsync(a2, i2)).Version);
    }

    [Fact]
    public async Task UniquePositionConstraint_RejectsDuplicateForSameAccountAndInstrument()
    {
        var (a, i) = await PositionAsync();
        await using var db = CreateDbContext();
        db.Positions.Add(new Position
        {
            AccountId = a,
            InstrumentId = i,
            Quantity = 10,
            ReservedQuantity = 0,
            AverageCost = 100m
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(1, await CountPositionsAsync(a, i));
    }

    private static async Task<int> CountPositionsAsync(int accountId, int instrumentId)
    {
        await using var db = CreateDbContext();
        return await db.Positions.CountAsync(p => p.AccountId == accountId && p.InstrumentId == instrumentId);
    }
}
