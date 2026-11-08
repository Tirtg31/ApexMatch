using Microsoft.EntityFrameworkCore;
using TradingEngine.Core.Entities;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradingEngine.Tests.Persistence;

/// <summary>
/// Real Oracle integration tests. Set TRADING_TEST_DB to a DEDICATED migrated test schema.
/// Tests insert unique instruments and remove only their own rows.
/// Never point these tests at production or shared development data.
/// </summary>
[Trait("Category", "OracleIntegration")]
public sealed class InstrumentRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly List<int> createdIds = new();

    private static TradingDbContext CreateDbContext()
    {
        string connectionString = "User Id=trading;Password=trade_123;Data Source=localhost:1521/XEPDB1";

        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseOracle(connectionString, oracle => oracle.UseOracleSQLCompatibility(
                OracleSQLCompatibility.DatabaseVersion21))
            .Options;
        return new TradingDbContext(options);
    }

    private async Task<TradeInstrument> SeedAsync(bool active = true)
    {
        await using var db = CreateDbContext();
        var entity = new TradeInstrument
        {
            InstrumentName = $"Instrument test {Guid.NewGuid():N}",
            InstrumentSymbol = $"T{Guid.NewGuid():N}"[..18],
            IsActive = active
        };
        db.TradeInstruments.Add(entity);
        await db.SaveChangesAsync();
        createdIds.Add(entity.InstrumentId);
        return entity;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (createdIds.Count == 0) return;
        await using var db = CreateDbContext();
        await db.TradeInstruments
            .Where(x => createdIds.Contains(x.InstrumentId))
            .ExecuteDeleteAsync();
    }

    [Fact]
    public async Task GetByIdAsync_Existing_ReturnsPersistedInstrument()
    {
        var seeded = await SeedAsync();
        await using var db = CreateDbContext();
        var repo = new InstrumentRepository(db);

        var actual = await repo.GetByIdAsync(seeded.InstrumentId);

        Assert.NotNull(actual);
        Assert.Equal(seeded.InstrumentId, actual.InstrumentId);
        Assert.Equal(seeded.InstrumentName, actual.InstrumentName);
        Assert.Equal(seeded.InstrumentSymbol, actual.InstrumentSymbol);
        Assert.True(actual.IsActive);
    }

    [Fact]
    public async Task GetByIdAsync_Missing_ReturnsNull()
    {
        await using var db = CreateDbContext();
        var repo = new InstrumentRepository(db);

        // The negative value is not a generated database ID.
        var actual = await repo.GetByIdAsync(-123456);

        Assert.Null(actual);
    }

    [Fact]
    public async Task GetByIdAsync_InactiveInstrument_IsStillReturned()
    {
        var seeded = await SeedAsync(active: false);
        await using var db = CreateDbContext();
        var repo = new InstrumentRepository(db);

        var actual = await repo.GetByIdAsync(seeded.InstrumentId);

        Assert.NotNull(actual);
        Assert.False(actual.IsActive);
    }

    [Fact]
    public async Task GetAllAsync_IncludesPersistedActiveAndInactiveInstruments()
    {
        var active = await SeedAsync(active: true);
        var inactive = await SeedAsync(active: false);
        await using var db = CreateDbContext();
        var repo = new InstrumentRepository(db);

        var all = await repo.GetAllAsync();

        Assert.Contains(all, x => x.InstrumentId == active.InstrumentId && x.IsActive);
        Assert.Contains(all, x => x.InstrumentId == inactive.InstrumentId && !x.IsActive);
    }

    [Fact]
    public async Task GetByIdAsync_AsNoTracking_DoesNotTrackReturnedEntity()
    {
        var seeded = await SeedAsync();
        await using var db = CreateDbContext();
        var repo = new InstrumentRepository(db);

        var result = await repo.GetByIdAsync(seeded.InstrumentId);

        Assert.NotNull(result);
        Assert.Equal(EntityState.Detached, db.Entry(result).State);
    }

    [Fact]
    public async Task GetAllAsync_AsNoTracking_DoesNotTrackReturnedEntities()
    {
        var seeded = await SeedAsync();
        await using var db = CreateDbContext();
        var repo = new InstrumentRepository(db);

        var all = await repo.GetAllAsync();
        var result = Assert.Single(all.Where(x => x.InstrumentId == seeded.InstrumentId));

        Assert.Equal(EntityState.Detached, db.Entry(result).State);
    }

    [Fact]
    public async Task GetByIdAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        await using var db = CreateDbContext();
        var repo = new InstrumentRepository(db);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repo.GetByIdAsync(-123456, cts.Token));
    }

    [Fact]
    public async Task GetAllAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        await using var db = CreateDbContext();
        var repo = new InstrumentRepository(db);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repo.GetAllAsync(cts.Token));
    }
}
