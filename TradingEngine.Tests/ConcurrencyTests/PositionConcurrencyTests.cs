using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Services;
using Xunit;

namespace TradingEngine.Tests.ConcurrencyTests;

/// <summary>
/// Concurrency and isolation tests for the in-memory PositionService.
/// Start with the current unlocked service; some tests are expected to fail.
/// Passing stress tests is not proof of thread safety.
/// </summary>
public class PositionConcurrencyTests
{
    private const int Account = 1;
    private const int Instrument = 101;

    private static void AssertValid(Position p)
    {
        Assert.True(p.Quantity > 0, "Stored position must have positive quantity.");
        Assert.InRange(p.ReservedQuantity, 0, p.Quantity);
        Assert.Equal(p.Quantity - p.ReservedQuantity, p.AvailableQuantity);
        Assert.True(p.AverageCost > 0);
    }

    // A single gate releases all scheduled workers together. It increases contention,
    // but cannot force a specific low-level interleaving.
    private static async Task RunTogetherAsync(int count, Action<int> work)
    {
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] tasks = Enumerable.Range(0, count)
            .Select(i => Task.Run(async () =>
            {
                await start.Task;
                work(i);
            }))
            .ToArray();
        start.SetResult(true);
        await Task.WhenAll(tasks);
    }

    // Collect expected business rejections while preserving unexpected exceptions.
    private static async Task<(int accepted, int rejected)> CompeteAsync(
        int count, Action work)
    {
        int accepted = 0, rejected = 0;
        await RunTogetherAsync(count, _ =>
        {
            try
            {
                work();
                Interlocked.Increment(ref accepted);
            }
            catch (ArgumentException)
            {
                Interlocked.Increment(ref rejected);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref rejected);
            }
        });
        return (accepted, rejected);
    }

    [Fact]
    public async Task ConcurrentAddSamePosition_ShouldAccumulateAllShares()
    {
        var service = new PositionService();
        service.AddPosition(Account, Instrument, 1, 100m);
        await RunTogetherAsync(100, _ => service.AddPosition(Account, Instrument, 1, 100m));
        Position p = Assert.IsType<Position>(service.GetPosition(Account, Instrument));
        Assert.Equal(101, p.Quantity);
        Assert.Equal(100m, p.AverageCost);
        AssertValid(p);
    }

    [Fact]
    public async Task ConcurrentAddSamePosition_ShouldCalculateWeightedAverageCost()
    {
        var service = new PositionService();
        service.AddPosition(Account, Instrument, 10, 100m);
        await RunTogetherAsync(100, i => service.AddPosition(Account, Instrument, 1, i % 2 == 0 ? 200m : 300m));
        Position p = Assert.IsType<Position>(service.GetPosition(Account, Instrument));
        Assert.Equal(110, p.Quantity);
        Assert.InRange(p.AverageCost, 26000m / 110m - 0.000001m, 26000m / 110m + 0.000001m);
        AssertValid(p);
    }

    [Fact]
    public async Task ConcurrentCreateDistinctPositions_ShouldPreserveUniqueIds()
    {
        var service = new PositionService();
        await RunTogetherAsync(100, i => service.AddPosition(i + 1, Instrument, 10, 100m));
        Position[] positions = Enumerable.Range(1, 100)
            .Select(id => Assert.IsType<Position>(service.GetPosition(id, Instrument)))
            .ToArray();
        Assert.Equal(100, positions.Select(p => p.PositionId).Distinct().Count());
        Assert.All(positions, AssertValid);
    }

    [Fact]
    public async Task ConcurrentCreateDifferentInstruments_ShouldNotLoseEntries()
    {
        var service = new PositionService();
        await RunTogetherAsync(100, i => service.AddPosition(Account, i + 1, 1, 99m));
        var positions = service.GetPositionsByAccount(Account);
        Assert.Equal(100, positions.Count);
        Assert.Equal(100, positions.Values.Sum(p => p.Quantity));
        Assert.All(positions.Values, AssertValid);
    }

    [Fact]
    public async Task ConcurrentReservations_ShouldAcceptOnlyOneOfTwoRequests()
    {
        var service = new PositionService();
        service.AddPosition(Account, Instrument, 100, 100m);
        var result = await CompeteAsync(2, () => service.ReserveQuantity(Account, Instrument, 80));
        Assert.Equal(1, result.accepted);
        Assert.Equal(1, result.rejected);
        Position p = Assert.IsType<Position>(service.GetPosition(Account, Instrument));
        Assert.Equal(80, p.ReservedQuantity);
        Assert.Equal(20, p.AvailableQuantity);
        AssertValid(p);
    }

    [Fact]
    public async Task HundredConcurrentReservations_ShouldAcceptExactlyTen()
    {
        var service = new PositionService();
        service.AddPosition(Account, Instrument, 100, 100m);
        var result = await CompeteAsync(100, () => service.ReserveQuantity(Account, Instrument, 10));
        Assert.Equal(10, result.accepted);
        Assert.Equal(90, result.rejected);
        Position p = Assert.IsType<Position>(service.GetPosition(Account, Instrument));
        Assert.Equal(100, p.ReservedQuantity);
        Assert.Equal(0, p.AvailableQuantity);
        AssertValid(p);
    }

    [Fact]
    public async Task ConcurrentReleases_ShouldNeverReleaseMoreThanReserved()
    {
        var service = new PositionService();
        service.AddPosition(Account, Instrument, 100, 100m);
        service.ReserveQuantity(Account, Instrument, 50);
        var result = await CompeteAsync(100, () => service.ReleaseReservedQuantity(Account, Instrument, 10));
        Assert.Equal(5, result.accepted);
        Assert.Equal(95, result.rejected);
        Position p = Assert.IsType<Position>(service.GetPosition(Account, Instrument));
        Assert.Equal(0, p.ReservedQuantity);
        Assert.Equal(100, p.AvailableQuantity);
        AssertValid(p);
    }

    [Fact]
    public async Task ConcurrentReductions_ShouldNeverSellMoreThanAvailable()
    {
        var service = new PositionService();
        service.AddPosition(Account, Instrument, 100, 100m);
        var result = await CompeteAsync(100, () => service.ReducePosition(Account, Instrument, 10));
        Assert.Equal(10, result.accepted);
        Assert.Equal(90, result.rejected);
        Assert.Null(service.GetPosition(Account, Instrument));
    }

    [Fact]
    public async Task ConcurrentReductions_MustRespectReservedQuantity()
    {
        var service = new PositionService();
        service.AddPosition(Account, Instrument, 100, 100m);
        service.ReserveQuantity(Account, Instrument, 70);
        var result = await CompeteAsync(100, () => service.ReducePosition(Account, Instrument, 10));
        Assert.Equal(3, result.accepted);
        Assert.Equal(97, result.rejected);
        Position p = Assert.IsType<Position>(service.GetPosition(Account, Instrument));
        Assert.Equal(70, p.Quantity);
        Assert.Equal(70, p.ReservedQuantity);
        AssertValid(p);
    }

    [Fact]
    public async Task ConcurrentSellSettlements_ShouldNotSettleMoreThanReserved()
    {
        var service = new PositionService();
        service.AddPosition(Account, Instrument, 100, 100m);
        service.ReserveQuantity(Account, Instrument, 100);
        var result = await CompeteAsync(100, () => service.SettleSellPosition(Account, Instrument, 10));
        Assert.Equal(10, result.accepted);
        Assert.Equal(90, result.rejected);
        Assert.Null(service.GetPosition(Account, Instrument));
    }

    [Fact]
    public async Task ConcurrentReleaseAndSettlement_ShouldConsumeEachReservedShareOnce()
    {
        var service = new PositionService();
        service.AddPosition(Account, Instrument, 100, 100m);
        service.ReserveQuantity(Account, Instrument, 50);
        int released = 0, settled = 0;
        await RunTogetherAsync(100, i =>
        {
            try
            {
                if (i % 2 == 0)
                {
                    service.ReleaseReservedQuantity(Account, Instrument, 1);
                    Interlocked.Increment(ref released);
                }
                else
                {
                    service.SettleSellPosition(Account, Instrument, 1);
                    Interlocked.Increment(ref settled);
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        });
        Position p = Assert.IsType<Position>(service.GetPosition(Account, Instrument));
        Assert.Equal(50, released + settled);
        Assert.Equal(100 - settled, p.Quantity);
        Assert.Equal(0, p.ReservedQuantity);
        AssertValid(p);
    }

    [Fact]
    public async Task ConcurrentAddsAndReductions_ShouldPreserveNetQuantity()
    {
        var service = new PositionService();
        service.AddPosition(Account, Instrument, 1000, 50m);
        await RunTogetherAsync(200, i =>
        {
            if (i % 2 == 0) service.AddPosition(Account, Instrument, 1, 50m);
            else service.ReducePosition(Account, Instrument, 1);
        });
        Position p = Assert.IsType<Position>(service.GetPosition(Account, Instrument));
        Assert.Equal(1000, p.Quantity);
        Assert.Equal(50m, p.AverageCost);
        AssertValid(p);
    }

    [Fact]
    public async Task ConcurrentReservationsAndAdditions_ShouldMaintainInvariants()
    {
        var service = new PositionService();
        service.AddPosition(Account, Instrument, 100, 100m);
        int successfulReservations = 0;
        await RunTogetherAsync(200, i =>
        {
            if (i % 2 == 0) service.AddPosition(Account, Instrument, 1, 100m);
            else
            {
                try
                {
                    service.ReserveQuantity(Account, Instrument, 1);
                    Interlocked.Increment(ref successfulReservations);
                }
                catch (ArgumentException) { }
            }
        });
        Position p = Assert.IsType<Position>(service.GetPosition(Account, Instrument));
        Assert.Equal(200, p.Quantity);
        Assert.Equal(successfulReservations, p.ReservedQuantity);
        AssertValid(p);
    }

    [Fact]
    public async Task ConcurrentReadsAndUpdates_ShouldReturnValidSnapshots()
    {
        var service = new PositionService();
        service.AddPosition(Account, Instrument, 100, 100m);
        await RunTogetherAsync(100, i =>
        {
            if (i % 2 == 0)
            {
                service.AddPosition(Account, Instrument, 1, 100m);
            }
            else
            {
                Position p = Assert.IsType<Position>(service.GetPosition(Account, Instrument));
                AssertValid(p);
                Assert.Equal(p.AvailableQuantity, p.Quantity - p.ReservedQuantity);
            }
        });
        Assert.Equal(150, service.GetPosition(Account, Instrument)!.Quantity);
    }

    [Fact]
    public async Task ConcurrentEnumerationAndInsertion_ShouldNotThrowOrCorruptResults()
    {
        var service = new PositionService();
        service.AddPosition(Account, 1, 1, 100m);
        await RunTogetherAsync(100, i =>
        {
            if (i % 2 == 0) service.AddPosition(Account, i + 2, 1, 100m);
            else
            {
                var snapshot = service.GetPositionsByAccount(Account);
                Assert.All(snapshot.Values, AssertValid);
            }
        });
        Assert.Equal(51, service.GetPositionsByAccount(Account).Count);
    }

    [Fact]
    public async Task ConcurrentRemoveAndRecreate_ShouldRetainOneValidPosition()
    {
        var service = new PositionService();
        service.AddPosition(Account, Instrument, 1000, 100m);
        await RunTogetherAsync(200, i =>
        {
            if (i % 2 == 0) service.AddPosition(Account, Instrument, 1, 100m);
            else service.ReducePosition(Account, Instrument, 1);
        });
        Position p = Assert.IsType<Position>(service.GetPosition(Account, Instrument));
        Assert.Equal(1000, p.Quantity);
        AssertValid(p);
    }

    // Defensive-copy contract tests. These fail on the original PositionService
    // even in single-threaded execution; locks alone will not fix them.
    [Fact]
    public void GetPosition_ShouldNotExposeMutableInternalReference()
    {
        var service = new PositionService();
        service.AddPosition(Account, Instrument, 100, 100m);
        Position fetched = Assert.IsType<Position>(service.GetPosition(Account, Instrument));
        fetched.Quantity = 9999;
        fetched.ReservedQuantity = 9999;
        Position actual = Assert.IsType<Position>(service.GetPosition(Account, Instrument));
        Assert.NotSame(fetched, actual);
        Assert.Equal(100, actual.Quantity);
        Assert.Equal(0, actual.ReservedQuantity);
    }

    [Fact]
    public void GetPositionsByAccount_ShouldNotExposeMutableInternalValues()
    {
        var service = new PositionService();
        service.AddPosition(Account, Instrument, 100, 100m);
        Dictionary<int, Position> fetched = service.GetPositionsByAccount(Account);
        fetched[Instrument].Quantity = 9999;
        fetched[Instrument].ReservedQuantity = 9999;
        Position actual = Assert.IsType<Position>(service.GetPosition(Account, Instrument));
        Assert.NotSame(fetched[Instrument], actual);
        Assert.Equal(100, actual.Quantity);
        Assert.Equal(0, actual.ReservedQuantity);
    }

    [Fact]
    public async Task IndependentAccounts_ShouldNotInterfereWithEachOther()
    {
        var service = new PositionService();
        for (int i = 1; i <= 50; i++) service.AddPosition(i, Instrument, 100, 50m);
        await RunTogetherAsync(50, i =>
        {
            int account = i + 1;
            service.ReserveQuantity(account, Instrument, 40);
            service.SettleSellPosition(account, Instrument, 25);
            service.ReleaseReservedQuantity(account, Instrument, 15);
        });
        for (int account = 1; account <= 50; account++)
        {
            Position p = Assert.IsType<Position>(service.GetPosition(account, Instrument));
            Assert.Equal(75, p.Quantity);
            Assert.Equal(0, p.ReservedQuantity);
            AssertValid(p);
        }
    }
}
