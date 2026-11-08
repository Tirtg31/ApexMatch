using Microsoft.AspNetCore.Mvc;
using TradingEngine.Api.Controllers;
using TradingEngine.Api.DTOs.Position;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Interfaces.Repositories;
using Xunit;

namespace TradingEngine.Tests.Api;

/// <summary>
/// Tests controller action behavior without an HTTP host or Oracle database.
/// Attribute routing, model binding, and Oracle/EF Core integration are not exercised here.
/// </summary>
public sealed class PositionsControllerTests
{
    private static Position NewPosition(
        int accountId = 521,
        int instrumentId = 10,
        int quantity = 100,
        int reserved = 30,
        decimal averageCost = 450m) => new()
        {
            PositionId = instrumentId + 1000,
            AccountId = accountId,
            InstrumentId = instrumentId,
            Quantity = quantity,
            ReservedQuantity = reserved,
            AverageCost = averageCost
        };

    [Fact]
    public async Task GetAllPositionsById_NoPositions_Returns200WithEmptyList()
    {
        var repo = new FakePositionRepository();
        var controller = new PositionsController(repo);

        var result = await controller.GetAllPositionsById(521, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(Assert.IsType<List<PositionResponseDto>>(ok.Value));
        Assert.Equal(1, repo.GetByAccountCalls);
        Assert.Equal(521, repo.LastAccountId);
    }

    [Fact]
    public async Task GetAllPositionsById_OnePosition_MapsEveryField()
    {
        var repo = new FakePositionRepository(NewPosition());
        var controller = new PositionsController(repo);

        var result = await controller.GetAllPositionsById(521, CancellationToken.None);

        var dto = Assert.Single(Assert.IsType<List<PositionResponseDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value));
        Assert.Equal(521, dto.AccountId);
        Assert.Equal(10, dto.InstrumentId);
        Assert.Equal(100, dto.Quantity);
        Assert.Equal(30, dto.ReservedQuantity);
        Assert.Equal(70, dto.AvailableQuantity);
        Assert.Equal(450m, dto.AverageCost);
    }

    [Fact]
    public async Task GetAllPositionsById_MultiplePositions_ReturnsOnlyRequestedAccount()
    {
        var repo = new FakePositionRepository(
            NewPosition(521, 10, 100, 20),
            NewPosition(521, 11, 50, 5),
            NewPosition(522, 12, 40, 0));
        var controller = new PositionsController(repo);

        var result = await controller.GetAllPositionsById(521, CancellationToken.None);

        var dtos = Assert.IsType<List<PositionResponseDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, dtos.Count);
        Assert.Equal(new[] { 10, 11 }, dtos.Select(x => x.InstrumentId));
        Assert.All(dtos, x => Assert.Equal(521, x.AccountId));
        Assert.Equal(80, dtos[0].AvailableQuantity);
        Assert.Equal(45, dtos[1].AvailableQuantity);
    }

    [Theory]
    [InlineData(100, 0, 100)]
    [InlineData(100, 100, 0)]
    [InlineData(100, 30, 70)]
    public async Task GetAllPositionsById_AvailableQuantity_IsQuantityMinusReserved(
        int quantity, int reserved, int expectedAvailable)
    {
        var controller = new PositionsController(new FakePositionRepository(
            NewPosition(quantity: quantity, reserved: reserved)));

        var result = await controller.GetAllPositionsById(521, CancellationToken.None);

        var dto = Assert.Single(Assert.IsType<List<PositionResponseDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value));
        Assert.Equal(expectedAvailable, dto.AvailableQuantity);
    }

    [Fact]
    public async Task GetAllPositionsById_ForwardsAccountIdAndCancellationToken()
    {
        var repo = new FakePositionRepository();
        using var cts = new CancellationTokenSource();
        var controller = new PositionsController(repo);

        await controller.GetAllPositionsById(777, cts.Token);

        Assert.Equal(777, repo.LastAccountId);
        Assert.Equal(cts.Token, repo.LastToken);
        Assert.Equal(1, repo.GetByAccountCalls);
    }

    [Fact]
    public async Task GetAllPositionsById_RepositoryThrows_PropagatesException()
    {
        var repo = new FakePositionRepository { Failure = new InvalidOperationException("Database unavailable") };
        var controller = new PositionsController(repo);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.GetAllPositionsById(521, CancellationToken.None));

        Assert.Equal("Database unavailable", exception.Message);
    }

    [Fact]
    public async Task GetPosition_Existing_Returns200AndMapsEveryField()
    {
        var repo = new FakePositionRepository(NewPosition(521, 20, 250, 75, 123.45m));
        var controller = new PositionsController(repo);

        var result = await controller.GetPosition(521, 20, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<PositionResponseDto>(ok.Value);
        Assert.Equal(521, dto.AccountId);
        Assert.Equal(20, dto.InstrumentId);
        Assert.Equal(250, dto.Quantity);
        Assert.Equal(75, dto.ReservedQuantity);
        Assert.Equal(175, dto.AvailableQuantity);
        Assert.Equal(123.45m, dto.AverageCost);
    }

    [Fact]
    public async Task GetPosition_Missing_Returns404()
    {
        var controller = new PositionsController(new FakePositionRepository());

        var result = await controller.GetPosition(521, 999999, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Contains("999999", Assert.IsType<string>(notFound.Value));
    }

    [Fact]
    public async Task GetPosition_SameInstrumentDifferentAccount_Returns404()
    {
        var repo = new FakePositionRepository(NewPosition(522, 10));
        var controller = new PositionsController(repo);

        var result = await controller.GetPosition(521, 10, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetPosition_ForwardsBothIdsAndCancellationToken()
    {
        var repo = new FakePositionRepository();
        using var cts = new CancellationTokenSource();
        var controller = new PositionsController(repo);

        await controller.GetPosition(123, 456, cts.Token);

        Assert.Equal(123, repo.LastAccountId);
        Assert.Equal(456, repo.LastInstrumentId);
        Assert.Equal(cts.Token, repo.LastToken);
        Assert.Equal(1, repo.GetSingleCalls);
    }

    [Fact]
    public async Task GetPosition_RepositoryThrows_PropagatesException()
    {
        var repo = new FakePositionRepository { Failure = new InvalidOperationException("Database unavailable") };
        var controller = new PositionsController(repo);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.GetPosition(521, 10, CancellationToken.None));

        Assert.Equal("Database unavailable", exception.Message);
    }

    [Fact]
    public async Task GetPosition_AlreadyCancelledToken_IsForwarded()
    {
        // Controller forwards cancellation; repository decides whether to honor it.
        var repo = new FakePositionRepository();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var controller = new PositionsController(repo);

        await controller.GetPosition(521, 10, cts.Token);

        Assert.True(repo.LastToken.IsCancellationRequested);
    }

    [Fact]
    public async Task GetAllPositionsById_AlreadyCancelledToken_IsForwarded()
    {
        var repo = new FakePositionRepository();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var controller = new PositionsController(repo);

        await controller.GetAllPositionsById(521, cts.Token);

        Assert.True(repo.LastToken.IsCancellationRequested);
    }

    [Fact]
    public async Task GetPosition_ModifyingDto_DoesNotModifyEntity()
    {
        var entity = NewPosition(521, 10, 100, 30, 450m);
        var controller = new PositionsController(new FakePositionRepository(entity));

        var result = await controller.GetPosition(521, 10, CancellationToken.None);
        var dto = Assert.IsType<PositionResponseDto>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        dto.Quantity = 999;
        dto.ReservedQuantity = 999;
        dto.AverageCost = 1m;

        Assert.Equal(100, entity.Quantity);
        Assert.Equal(30, entity.ReservedQuantity);
        Assert.Equal(450m, entity.AverageCost);
        Assert.Equal(70, entity.AvailableQuantity);
    }

    [Fact]
    public async Task GetAllPositionsById_DoesNotWriteToRepository()
    {
        var repo = new FakePositionRepository(NewPosition());
        var controller = new PositionsController(repo);

        await controller.GetAllPositionsById(521, CancellationToken.None);

        Assert.Equal(0, repo.AddCalls);
        Assert.Equal(0, repo.GetSingleCalls);
    }

    [Fact]
    public async Task GetPosition_DoesNotWriteToRepository()
    {
        var repo = new FakePositionRepository(NewPosition());
        var controller = new PositionsController(repo);

        await controller.GetPosition(521, 10, CancellationToken.None);

        Assert.Equal(0, repo.AddCalls);
        Assert.Equal(0, repo.GetByAccountCalls);
    }

    private sealed class FakePositionRepository : IPositionRepository
    {
        private readonly List<Position> positions;

        public FakePositionRepository(params Position[] positions)
        {
            this.positions = positions.ToList();
        }

        public Exception? Failure { get; set; }
        public int GetByAccountCalls { get; private set; }
        public int GetSingleCalls { get; private set; }
        public int AddCalls { get; private set; }
        public int? LastAccountId { get; private set; }
        public int? LastInstrumentId { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task<List<Position>> GetByAccountIdAsync(
            int accountId, CancellationToken ct = default)
        {
            GetByAccountCalls++;
            LastAccountId = accountId;
            LastToken = ct;
            if (Failure is not null)
                return Task.FromException<List<Position>>(Failure);

            return Task.FromResult(positions.Where(p => p.AccountId == accountId).ToList());
        }

        public Task<Position?> GetByAccountAndInstrumentAsync(
            int accountId, int instrumentId, CancellationToken ct = default)
        {
            GetSingleCalls++;
            LastAccountId = accountId;
            LastInstrumentId = instrumentId;
            LastToken = ct;
            if (Failure is not null)
                return Task.FromException<Position?>(Failure);

            return Task.FromResult(positions.FirstOrDefault(p =>
                p.AccountId == accountId && p.InstrumentId == instrumentId));
        }

        public Task AddAsync(Position position, CancellationToken ct = default)
        {
            AddCalls++;
            positions.Add(position);
            return Task.CompletedTask;
        }
    }
}
