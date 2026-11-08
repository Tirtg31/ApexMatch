using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Core.Entities;
using TradingEngine.Infrastructure.Persistence;
using Xunit;

namespace TradingEngine.Tests.Persistence;

/// <summary>
/// ACTUAL overlapping execution, not only sequential stale-write tests.
/// Each worker owns its own DbContext. A TaskCompletionSource barrier guarantees
/// all workers load the same version BEFORE any worker attempts SaveChangesAsync.
/// These tests intentionally do NOT retry conflicts: one competing write should
/// win and the others should fail. Oracle may surface provider-specific errors
/// for overlapping transactions, so assertions focus on safety + persisted state.
/// Requires dedicated Oracle test DB and applied Account/Position Version migrations.
/// </summary>
public sealed class SimultaneousAccountAndPositionIntegrationTests : IAsyncLifetime
{
    private readonly List<int> _accountIds = new();
    private readonly List<int> _instrumentIds = new();

    private static TradingDbContext Db()
    {
        string cs = "User Id=trading;Password=trade_123;Data Source=localhost:1521/XEPDB1";
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseOracle(cs, o => o.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion21))
            .Options;
        return new TradingDbContext(options);
    }

    private async Task<int> AddAccountAsync(decimal cash = 10000m)
    {
        await using var db = Db();
        var a = new Account
        {
            AccountName = $"PAR_A_{Guid.NewGuid():N}",
            CashBalance = cash,
            AvailableCash = cash,
            ReservedCash = 0m,
            IsActive = true
        };
        db.Accounts.Add(a);
        await db.SaveChangesAsync();
        _accountIds.Add(a.AccountId);
        return a.AccountId;
    }

    private async Task<(int accountId, int instrumentId)> AddPositionAsync(int quantity = 100)
    {
        int accountId = await AddAccountAsync();
        await using var db = Db();
        var instrument = new TradeInstrument
        {
            InstrumentName = $"PAR Instrument {Guid.NewGuid():N}",
            InstrumentSymbol = $"P{Guid.NewGuid():N}"[..18],
            IsActive = true
        };
        db.TradeInstruments.Add(instrument);
        await db.SaveChangesAsync();
        _instrumentIds.Add(instrument.InstrumentId);
        var pos = new Position
        {
            AccountId = accountId,
            InstrumentId = instrument.InstrumentId,
            Quantity = quantity,
            ReservedQuantity = 0,
            AverageCost = 100m
        };
        db.Positions.Add(pos);
        await db.SaveChangesAsync();
        return (accountId, instrument.InstrumentId);
    }

    private static async Task<Account> ReadAccount(int id)
    {
        await using var db = Db();
        return await db.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == id);
    }

    private static async Task<Position> ReadPosition(int accountId, int instrumentId)
    {
        await using var db = Db();
        return await db.Positions.AsNoTracking().SingleAsync(p =>
            p.AccountId == accountId && p.InstrumentId == instrumentId);
    }

    // Barrier is asynchronous: all workers must complete their read before saves begin.
    // Each worker returns its own outcome; no DbContext is ever shared across tasks.
    private static async Task<Exception?[]> RunAtSameStartingVersionAsync<T>(
        int workerCount,
        Func<TradingDbContext, Task<T>> load,
        Action<T, int> change)
        where T : class
    {
        int ready = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var registration = timeout.Token.Register(() => gate.TrySetCanceled(timeout.Token));

        async Task<Exception?> Worker(int index)
        {
            await using var db = Db();
            try
            {
                T entity = await load(db);
                change(entity, index);
                if (Interlocked.Increment(ref ready) == workerCount)
                    gate.TrySetResult(true);
                await gate.Task;
                await db.SaveChangesAsync();
                return null;
            }
            catch (Exception ex)
            {
                // Prevent other tasks hanging if a participant fails before the barrier.
                gate.TrySetException(ex);
                return ex;
            }
        }

        return await Task.WhenAll(Enumerable.Range(0, workerCount).Select(Worker));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var db = Db();
        if (_accountIds.Count > 0)
            await db.Positions.Where(p => _accountIds.Contains(p.AccountId)).ExecuteDeleteAsync();
        if (_instrumentIds.Count > 0)
            await db.TradeInstruments.Where(i => _instrumentIds.Contains(i.InstrumentId)).ExecuteDeleteAsync();
        if (_accountIds.Count > 0)
            await db.Accounts.Where(a => _accountIds.Contains(a.AccountId)).ExecuteDeleteAsync();
    }

    // 1. Two concurrent reservations read the same account version.
    // Only one may reserve 7000 out of 10000 (without retry).
    [Fact]
    public async Task Account_TwoSimultaneousReservations_ExactlyOneCommits()
    {
        int id = await AddAccountAsync();
        var outcomes = await RunAtSameStartingVersionAsync(2,
            db => db.Accounts.SingleAsync(a => a.AccountId == id),
            (a, _) =>
            {
                Assert.True(a.AvailableCash >= 7000m);
                a.AvailableCash -= 7000m;
                a.ReservedCash += 7000m;
            });
        Assert.Equal(1, outcomes.Count(e => e == null));
        Assert.Equal(1, outcomes.Count(e => e != null));
        var saved = await ReadAccount(id);
        Assert.Equal(10000m, saved.CashBalance);
        Assert.Equal(7000m, saved.ReservedCash);
        Assert.Equal(3000m, saved.AvailableCash);
        Assert.Equal(2L, saved.Version);
    }

    // 2. Many simultaneous deposits must not silently overwrite each other.
    // Without retry, a single same-version update is expected to commit.
    [Fact]
    public async Task Account_TenSimultaneousDeposits_NoLostAcknowledgedUpdates()
    {
        int id = await AddAccountAsync();
        var outcomes = await RunAtSameStartingVersionAsync(10,
            db => db.Accounts.SingleAsync(a => a.AccountId == id),
            (a, _) => { a.CashBalance += 100m; a.AvailableCash += 100m; });
        int succeeded = outcomes.Count(e => e is null);
        Assert.Equal(1, succeeded);
        var saved = await ReadAccount(id);
        Assert.Equal(10000m + succeeded * 100m, saved.CashBalance);
        Assert.Equal(saved.CashBalance, saved.AvailableCash + saved.ReservedCash);
        Assert.Equal(1L + succeeded, saved.Version);
    }

    // 3. Two concurrent withdrawals must not both spend the same funds.
    [Fact]
    public async Task Account_TwoSimultaneousWithdrawals_ExactlyOneCommits()
    {
        int id = await AddAccountAsync();
        var outcomes = await RunAtSameStartingVersionAsync(2,
            db => db.Accounts.SingleAsync(a => a.AccountId == id),
            (a, _) =>
            {
                Assert.True(a.AvailableCash >= 7000m);
                a.CashBalance -= 7000m;
                a.AvailableCash -= 7000m;
            });
        Assert.Equal(1, outcomes.Count(e => e is null));
        var saved = await ReadAccount(id);
        Assert.Equal(3000m, saved.CashBalance);
        Assert.Equal(3000m, saved.AvailableCash);
        Assert.Equal(2L, saved.Version);
    }

    // 4. One reserve and one withdrawal conflict on the same account version.
    [Fact]
    public async Task Account_SimultaneousReserveAndWithdraw_OnlyOneCommits()
    {
        int id = await AddAccountAsync();
        var outcomes = await RunAtSameStartingVersionAsync(2,
            db => db.Accounts.SingleAsync(a => a.AccountId == id),
            (a, index) =>
            {
                if (index == 0) { a.AvailableCash -= 7000m; a.ReservedCash += 7000m; }
                else { a.CashBalance -= 7000m; a.AvailableCash -= 7000m; }
            });
        Assert.Equal(1, outcomes.Count(e => e is null));
        var saved = await ReadAccount(id);
        Assert.Equal(saved.CashBalance, saved.AvailableCash + saved.ReservedCash);
        Assert.Equal(3000m, saved.AvailableCash);
        Assert.Equal(2L, saved.Version);
    }

    // 5. Simultaneous reservations for 70 of 100 shares must not both commit.
    [Fact]
    public async Task Position_TwoSimultaneousReservations_ExactlyOneCommits()
    {
        var (accountId, instrumentId) = await AddPositionAsync();
        var outcomes = await RunAtSameStartingVersionAsync(2,
            db => db.Positions.SingleAsync(p => p.AccountId == accountId && p.InstrumentId == instrumentId),
            (p, _) =>
            {
                Assert.True(p.AvailableQuantity >= 70);
                p.ReservedQuantity += 70;
            });
        Assert.Equal(1, outcomes.Count(e => e is null));
        var saved = await ReadPosition(accountId, instrumentId);
        Assert.Equal(100, saved.Quantity);
        Assert.Equal(70, saved.ReservedQuantity);
        Assert.Equal(30, saved.AvailableQuantity);
        Assert.Equal(2L, saved.Version);
    }

    // 6. Ten simultaneous reservations target the SAME version. One wins;
    // remaining requests must not return success without persisting.
    [Fact]
    public async Task Position_TenSimultaneousReservations_NoOverReservation()
    {
        var (accountId, instrumentId) = await AddPositionAsync(quantity: 50);
        var outcomes = await RunAtSameStartingVersionAsync(10,
            db => db.Positions.SingleAsync(p => p.AccountId == accountId && p.InstrumentId == instrumentId),
            (p, _) =>
            {
                Assert.True(p.AvailableQuantity >= 10);
                p.ReservedQuantity += 10;
            });
        int succeeded = outcomes.Count(e => e is null);
        Assert.Equal(1, succeeded); // no retry in these tests
        var saved = await ReadPosition(accountId, instrumentId);
        Assert.Equal(50, saved.Quantity);
        Assert.Equal(10 * succeeded, saved.ReservedQuantity);
        Assert.InRange(saved.AvailableQuantity, 0, 50);
        Assert.Equal(1L + succeeded, saved.Version);
    }

    // 7. Simultaneous increases cannot silently lose one confirmed increase.
    [Fact]
    public async Task Position_SimultaneousIncreases_NoLostAcknowledgedUpdates()
    {
        var (accountId, instrumentId) = await AddPositionAsync();
        var outcomes = await RunAtSameStartingVersionAsync(2,
            db => db.Positions.SingleAsync(p => p.AccountId == accountId && p.InstrumentId == instrumentId),
            (p, _) =>
            {
                int next = checked(p.Quantity + 20);
                p.AverageCost = (p.Quantity * p.AverageCost + 20 * 200m) / next;
                p.Quantity = next;
            });
        int succeeded = outcomes.Count(e => e is null);
        Assert.Equal(1, succeeded);
        var saved = await ReadPosition(accountId, instrumentId);
        Assert.Equal(100 + 20 * succeeded, saved.Quantity);
        decimal expectedAverageCost = Math.Round((100 * 100m + 20 * succeeded * 200m) / saved.Quantity,2, MidpointRounding.AwayFromZero);

        Assert.Equal(expectedAverageCost, saved.AverageCost);
        Assert.Equal(1L + succeeded, saved.Version);
    }

    // 8. Reservation and release compete on a position with 40 shares reserved.
    [Fact]
    public async Task Position_SimultaneousReserveAndRelease_PreservesInvariant()
    {
        var (accountId, instrumentId) = await AddPositionAsync();
        await using (var prepDb = Db())
        {
            var p = await prepDb.Positions.SingleAsync(p => p.AccountId == accountId && p.InstrumentId == instrumentId);
            p.ReservedQuantity = 40;
            await prepDb.SaveChangesAsync();
        }
        var outcomes = await RunAtSameStartingVersionAsync(2,
            db => db.Positions.SingleAsync(p => p.AccountId == accountId && p.InstrumentId == instrumentId),
            (p, index) => p.ReservedQuantity += index == 0 ? 30 : -20);
        Assert.Equal(1, outcomes.Count(e => e is null));
        var saved = await ReadPosition(accountId, instrumentId);
        Assert.Contains(saved.ReservedQuantity, new[] { 70, 20 });
        Assert.Equal(100 - saved.ReservedQuantity, saved.AvailableQuantity);
        Assert.Equal(3L, saved.Version);
    }

    // 9. Independent rows can be saved concurrently, without false conflicts.
    [Fact]
    public async Task Account_SimultaneousUpdatesToSeparateRows_BothCommit()
    {
        int first = await AddAccountAsync();
        int second = await AddAccountAsync();
        var ids = new[] { first, second };
        // For different IDs each worker must load its own row.
        int nextWorker = -1;
        var outcomes = await RunAtSameStartingVersionAsync(2,
            db =>
            {
                int index = Interlocked.Increment(ref nextWorker);
                return db.Accounts.SingleAsync(a => a.AccountId == ids[index]);
            },
            (a, _) => { a.CashBalance += 100m; a.AvailableCash += 100m; });
        Assert.All(outcomes, e => Assert.Null(e));
        Assert.Equal(10100m, (await ReadAccount(first)).CashBalance);
        Assert.Equal(10100m, (await ReadAccount(second)).CashBalance);
    }

    // 10. Independent position rows can be updated concurrently.
    [Fact]
    public async Task Position_SimultaneousUpdatesToSeparateRows_BothCommit()
    {
        var first = await AddPositionAsync();
        var second = await AddPositionAsync();
        var pairs = new[] { first, second };
        int nextWorker = -1;
        var outcomes = await RunAtSameStartingVersionAsync(2,
            db =>
            {
                int index = Interlocked.Increment(ref nextWorker);
                var pair = pairs[index];
                return db.Positions.SingleAsync(p => p.AccountId == pair.accountId && p.InstrumentId == pair.instrumentId);
            },
            (p, _) => p.ReservedQuantity += 10);
        Assert.All(outcomes, e => Assert.Null(e));
        Assert.Equal(10, (await ReadPosition(first.accountId, first.instrumentId)).ReservedQuantity);
        Assert.Equal(10, (await ReadPosition(second.accountId, second.instrumentId)).ReservedQuantity);
    }
}
