using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using TradingEngine.Api.Controllers;
using TradingEngine.Api.DTOs.Account;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Interfaces.Repositories;
using TradingEngine.Core.Services;
using Xunit;

namespace TradingEngine.Tests.Api;

/// <summary>
/// Controller/action tests, not full HTTP pipeline tests.
/// Uses real AccountService with in-memory fake infrastructure; no Oracle required.
/// DataAnnotations are tested separately because [ApiController] automatic 400s
/// only happen when requests pass through ASP.NET Core model binding.
/// </summary>
public sealed class AccountsControllerTests
{
    private readonly FakeAccountRepository repository = new();
    private readonly FakeUnitOfWork unitOfWork = new();
    private readonly AccountsController controller;
    private readonly TradingRuntimeState runtimeState = new();
    private readonly TradingCoordinator coordinator;

    public AccountsControllerTests()
    {
        var service = new AccountService(repository, unitOfWork);
        // These particular coordinator actions only access AccountService and the shared gate.
        // Other coordinator dependencies are intentionally unused in these tests.
        coordinator = new TradingCoordinator(
            null!, new OrderMatchingEngine(), null!, null!, service, runtimeState);
        controller = new AccountsController(service, coordinator);
        runtimeState.RequiresRecovery = false;
    }

    private static CreateAccountRequestDto Request(string name = "TradingAccount1", decimal balance = 100_000m) =>
        new() { AccountName = name, InitialBalance = balance };

    private static Account ExistingAccount(int id = 42) => new()
    {
        AccountId = id,
        AccountName = "ExistingAccount",
        CashBalance = 1000m,
        AvailableCash = 700m,
        ReservedCash = 300m,
        IsActive = true
    };

    private static List<ValidationResult> Validate(object instance)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        return results;
    }

    // TEST 1: POST should return 201 Created (CreatedAtActionResult), not 200 OK.
    [Fact]
    public async Task CreateAccount_ValidRequest_ReturnsCreatedAtAction()
    {
        var result = await controller.CreateAccount(Request(), CancellationToken.None);
        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(201, created.StatusCode);
        Assert.Equal(nameof(AccountsController.GetAccount), created.ActionName);
        var body = Assert.IsType<AccountResponseDto>(created.Value);
        Assert.Equal(body.AccountId, created.RouteValues!["id"]);
    }

    // TEST 2: POST maps every public response property correctly.
    [Fact]
    public async Task CreateAccount_ValidRequest_MapsFieldsAndInitialBalances()
    {
        var result = await controller.CreateAccount(Request("Buyer", 1250.25m), CancellationToken.None);
        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var body = Assert.IsType<AccountResponseDto>(created.Value);
        Assert.True(body.AccountId > 0);
        Assert.Equal("Buyer", body.AccountName);
        Assert.Equal(1250.25m, body.CashBalance);
        Assert.Equal(1250.25m, body.AvailableCash);
        Assert.Equal(0m, body.ReservedCash);
        Assert.True(body.IsActive);
    }

    // TEST 3: A new account is sent to the repository exactly once and saved once.
    [Fact]
    public async Task CreateAccount_ValidRequest_PersistsExactlyOnce()
    {
        await controller.CreateAccount(Request(), CancellationToken.None);
        Assert.Equal(1, repository.AddCount);
        Assert.Equal(1, unitOfWork.SaveCount);
        Assert.Single(repository.Stored);
    }

    // TEST 4: Creating an account with zero balance is allowed by the service.
    [Fact]
    public async Task CreateAccount_ZeroInitialBalance_IsAllowed()
    {
        var result = await controller.CreateAccount(Request(balance: 0m), CancellationToken.None);
        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var body = Assert.IsType<AccountResponseDto>(created.Value);
        Assert.Equal(0m, body.CashBalance);
        Assert.Equal(0m, body.AvailableCash);
    }

    // TEST 5: Negative balance is blocked by the domain service even in direct calls.
    [Fact]
    public async Task CreateAccount_NegativeBalance_ThrowsAndDoesNotPersist()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.CreateAccount(Request(balance: -1m), CancellationToken.None));
        Assert.Empty(repository.Stored);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    // TEST 6: Whitespace-only names are rejected by the domain service.
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\t")]
    public async Task CreateAccount_EmptyOrWhitespaceName_ThrowsAndDoesNotPersist(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.CreateAccount(Request(name), CancellationToken.None));
        Assert.Empty(repository.Stored);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    // TEST 7: GET returns 200 and maps all account fields, including reserved cash.
    [Fact]
    public async Task GetAccount_ExistingAccount_ReturnsOkWithCorrectBalances()
    {
        repository.Seed(ExistingAccount());
        var result = await controller.GetAccount(42, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<AccountResponseDto>(ok.Value);
        Assert.Equal(42, body.AccountId);
        Assert.Equal("ExistingAccount", body.AccountName);
        Assert.Equal(1000m, body.CashBalance);
        Assert.Equal(700m, body.AvailableCash);
        Assert.Equal(300m, body.ReservedCash);
        Assert.True(body.IsActive);
    }

    // TEST 8: Missing account produces 404, never a null-reference exception.
    [Fact]
    public async Task GetAccount_NonexistentAccount_ReturnsNotFound()
    {
        var result = await controller.GetAccount(9999, CancellationToken.None);
        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Contains("9999", Assert.IsType<string>(notFound.Value));
    }

    // TEST 9: GET passes the route ID unchanged to the repository.
    [Fact]
    public async Task GetAccount_UsesRequestedId()
    {
        await controller.GetAccount(123, CancellationToken.None);
        Assert.Equal(123, repository.LastRequestedId);
    }

    // TEST 10: GET never performs a write or invokes SaveChanges.
    [Fact]
    public async Task GetAccount_DoesNotSaveChanges()
    {
        repository.Seed(ExistingAccount());
        await controller.GetAccount(42, CancellationToken.None);
        Assert.Equal(0, repository.AddCount);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    // TEST 11: POST-created accounts can be retrieved through GET.
    [Fact]
    public async Task CreateAccount_ThenGetAccount_ReturnsSameAccount()
    {
        var post = await controller.CreateAccount(Request("RoundTrip", 500m), CancellationToken.None);
        var created = Assert.IsType<AccountResponseDto>(Assert.IsType<CreatedAtActionResult>(post.Result).Value);
        var get = await controller.GetAccount(created.AccountId, CancellationToken.None);
        var fetched = Assert.IsType<AccountResponseDto>(Assert.IsType<OkObjectResult>(get.Result).Value);
        Assert.Equal(created.AccountId, fetched.AccountId);
        Assert.Equal(created.AccountName, fetched.AccountName);
        Assert.Equal(created.CashBalance, fetched.CashBalance);
    }

    // TEST 12: DTO's [Required] attribute rejects empty account name.
    [Fact]
    public void CreateAccountRequest_EmptyName_FailsDataAnnotationsValidation()
    {
        var errors = Validate(Request(""));
        Assert.NotEmpty(errors);
        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(CreateAccountRequestDto.AccountName)));
    }

    // TEST 13: DTO's StringLength rejects names exceeding the DB field length.
    [Fact]
    public void CreateAccountRequest_NameLongerThan100_FailsValidation()
    {
        var errors = Validate(Request(new string('X', 101)));
        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(CreateAccountRequestDto.AccountName)));
    }

    // TEST 14: DTO's [Range] rejects negative initial deposits before controller invocation over HTTP.
    [Fact]
    public void CreateAccountRequest_NegativeBalance_FailsValidation()
    {
        var errors = Validate(Request(balance: -0.01m));
        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(CreateAccountRequestDto.InitialBalance)));
    }

    // TEST 15: Valid input passes DTO attribute validation.
    [Fact]
    public void CreateAccountRequest_ValidValues_PassValidation()
    {
        Assert.Empty(Validate(Request("Valid", 123m)));
    }

    // TEST 16: Cancellation token is forwarded by the controller to the repository.
    [Fact]
    public async Task GetAccount_ForwardsCancellationToken()
    {
        using var source = new CancellationTokenSource();
        await controller.GetAccount(45, source.Token);
        Assert.Equal(source.Token, repository.LastToken);
    }

    // DEPOSIT/WITHDRAWAL TESTS (direct controller calls with the real Coordinator + AccountService).
    // These tests do not exercise MVC model binding or global exception middleware.

    [Fact]
    public async Task Deposit_ValidAmount_ReturnsOkAndUpdatesAvailableAndTotalCash()
    {
        repository.Seed(ExistingAccount());
        var result = await controller.DepositAmount(42, new DepositRequestDto { Amount = 250m }, CancellationToken.None);
        Assert.IsType<OkResult>(result);
        var account = await repository.GetByIdAsync(42);
        Assert.Equal(1250m, account!.CashBalance);
        Assert.Equal(950m, account.AvailableCash);
        Assert.Equal(300m, account.ReservedCash);
        Assert.Equal(1, unitOfWork.TransactionCount);
    }

    [Fact]
    public async Task Withdraw_ValidAmount_ReturnsOkAndPreservesReservedCash()
    {
        repository.Seed(ExistingAccount());
        var result = await controller.WithdrawAmount(42, new WithdrawRequestDto { Amount = 200m }, CancellationToken.None);
        Assert.IsType<OkResult>(result);
        var account = await repository.GetByIdAsync(42);
        Assert.Equal(800m, account!.CashBalance);
        Assert.Equal(500m, account.AvailableCash);
        Assert.Equal(300m, account.ReservedCash);
        Assert.Equal(1, unitOfWork.TransactionCount);
    }

    [Fact]
    public async Task Withdraw_ExactlyAvailableCash_Succeeds()
    {
        repository.Seed(ExistingAccount());
        await controller.WithdrawAmount(42, new WithdrawRequestDto { Amount = 700m }, CancellationToken.None);
        var account = await repository.GetByIdAsync(42);
        Assert.Equal(300m, account!.CashBalance);
        Assert.Equal(0m, account.AvailableCash);
        Assert.Equal(300m, account.ReservedCash);
    }

    [Fact]
    public async Task Withdraw_MoreThanAvailableCash_ThrowsAndDoesNotChangeBalance()
    {
        repository.Seed(ExistingAccount());
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.WithdrawAmount(42, new WithdrawRequestDto { Amount = 701m }, CancellationToken.None));
        Assert.Contains("Insufficient funds", ex.Message);
        AssertBalances(42, 1000m, 700m, 300m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Deposit_NonPositiveAmount_ThrowsAndDoesNotChangeBalance(decimal amount)
    {
        repository.Seed(ExistingAccount());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.DepositAmount(42, new DepositRequestDto { Amount = amount }, CancellationToken.None));
        AssertBalances(42, 1000m, 700m, 300m);
        Assert.Equal(0, unitOfWork.TransactionCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Withdraw_NonPositiveAmount_ThrowsAndDoesNotChangeBalance(decimal amount)
    {
        repository.Seed(ExistingAccount());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.WithdrawAmount(42, new WithdrawRequestDto { Amount = amount }, CancellationToken.None));
        AssertBalances(42, 1000m, 700m, 300m);
        Assert.Equal(0, unitOfWork.TransactionCount);
    }

    [Fact]
    public async Task Deposit_MissingAccount_ThrowsWithoutCreatingAccount()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.DepositAmount(999, new DepositRequestDto { Amount = 10m }, CancellationToken.None));
        Assert.Empty(repository.Stored);
    }

    [Fact]
    public async Task Withdraw_MissingAccount_ThrowsWithoutCreatingAccount()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.WithdrawAmount(999, new WithdrawRequestDto { Amount = 10m }, CancellationToken.None));
        Assert.Empty(repository.Stored);
    }

    [Fact]
    public async Task Deposit_InactiveAccount_ThrowsWithoutChangingBalance()
    {
        var account = ExistingAccount();
        account.IsActive = false;
        repository.Seed(account);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.DepositAmount(42, new DepositRequestDto { Amount = 10m }, CancellationToken.None));
        AssertBalances(42, 1000m, 700m, 300m);
    }

    [Fact]
    public async Task Withdraw_InactiveAccount_ThrowsWithoutChangingBalance()
    {
        var account = ExistingAccount();
        account.IsActive = false;
        repository.Seed(account);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.WithdrawAmount(42, new WithdrawRequestDto { Amount = 10m }, CancellationToken.None));
        AssertBalances(42, 1000m, 700m, 300m);
    }

    [Fact]
    public async Task DepositAndWithdraw_WhenRecoveryRequired_RejectBothWithoutWrites()
    {
        repository.Seed(ExistingAccount());
        runtimeState.RequiresRecovery = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.DepositAmount(42, new DepositRequestDto { Amount = 50m }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.WithdrawAmount(42, new WithdrawRequestDto { Amount = 50m }, CancellationToken.None));
        AssertBalances(42, 1000m, 700m, 300m);
        Assert.Equal(0, unitOfWork.TransactionCount);
        Assert.Equal(1, runtimeState.TradingGate.CurrentCount);
    }

    [Fact]
    public async Task Deposit_WaitsForSharedTradingGate_UntilReleased()
    {
        repository.Seed(ExistingAccount());
        await runtimeState.TradingGate.WaitAsync();
        Task<IActionResult> pending;
        try
        {
            pending = controller.DepositAmount(42, new DepositRequestDto { Amount = 50m }, CancellationToken.None);
            Assert.False(pending.IsCompleted);
            AssertBalances(42, 1000m, 700m, 300m);
        }
        finally
        {
            runtimeState.TradingGate.Release();
        }
        Assert.IsType<OkResult>(await pending);
        AssertBalances(42, 1050m, 750m, 300m);
    }

    [Fact]
    public async Task Withdraw_WaitsForSharedTradingGate_UntilReleased()
    {
        repository.Seed(ExistingAccount());
        await runtimeState.TradingGate.WaitAsync();
        Task<IActionResult> pending;
        try
        {
            pending = controller.WithdrawAmount(42, new WithdrawRequestDto { Amount = 50m }, CancellationToken.None);
            Assert.False(pending.IsCompleted);
            AssertBalances(42, 1000m, 700m, 300m);
        }
        finally
        {
            runtimeState.TradingGate.Release();
        }
        Assert.IsType<OkResult>(await pending);
        AssertBalances(42, 950m, 650m, 300m);
    }

    [Fact]
    public async Task Deposit_IfValidationFails_ReleasesGateForNextOperation()
    {
        repository.Seed(ExistingAccount());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.DepositAmount(42, new DepositRequestDto { Amount = 0m }, CancellationToken.None));
        Assert.Equal(1, runtimeState.TradingGate.CurrentCount);
        Assert.IsType<OkResult>(await controller.DepositAmount(42, new DepositRequestDto { Amount = 10m }, CancellationToken.None));
        AssertBalances(42, 1010m, 710m, 300m);
    }

    [Fact]
    public async Task Withdraw_CancelledWhileWaiting_DoesNotReleaseSomeoneElsesGate()
    {
        repository.Seed(ExistingAccount());
        await runtimeState.TradingGate.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource();
            var pending = controller.WithdrawAmount(42, new WithdrawRequestDto { Amount = 10m }, cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(0, runtimeState.TradingGate.CurrentCount);
            AssertBalances(42, 1000m, 700m, 300m);
        }
        finally
        {
            runtimeState.TradingGate.Release();
        }
        Assert.Equal(1, runtimeState.TradingGate.CurrentCount);
    }

    [Fact]
    public async Task ConcurrentDepositsAndWithdrawals_ProduceConsistentBalances()
    {
        repository.Seed(ExistingAccount());
        // Many separate controller calls share the same coordinator gate.
        var operations = Enumerable.Range(0, 20)
            .Select(i => i % 2 == 0
                ? controller.DepositAmount(42, new DepositRequestDto { Amount = 10m }, CancellationToken.None)
                : controller.WithdrawAmount(42, new WithdrawRequestDto { Amount = 5m }, CancellationToken.None))
            .ToArray();
        await Task.WhenAll(operations);
        Assert.All(operations, task => Assert.IsType<OkResult>(task.Result));
        AssertBalances(42, 1050m, 750m, 300m);
        Assert.Equal(20, unitOfWork.TransactionCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.001)]
    public void DepositRequest_InvalidAmount_FailsValidation(decimal amount)
    {
        Assert.Contains(Validate(new DepositRequestDto { Amount = amount }),
            error => error.MemberNames.Contains(nameof(DepositRequestDto.Amount)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.001)]
    public void WithdrawRequest_InvalidAmount_FailsValidation(decimal amount)
    {
        Assert.Contains(Validate(new WithdrawRequestDto { Amount = amount }),
            error => error.MemberNames.Contains(nameof(WithdrawRequestDto.Amount)));
    }

    [Fact]
    public void DepositAndWithdrawRequest_ValidAmount_PassesValidation()
    {
        Assert.Empty(Validate(new DepositRequestDto { Amount = 0.01m }));
        Assert.Empty(Validate(new WithdrawRequestDto { Amount = 0.01m }));
    }

    private void AssertBalances(int accountId, decimal cash, decimal available, decimal reserved)
    {
        var account = repository.Stored.Single(a => a.AccountId == accountId);
        Assert.Equal(cash, account.CashBalance);
        Assert.Equal(available, account.AvailableCash);
        Assert.Equal(reserved, account.ReservedCash);
    }

    private sealed class FakeAccountRepository : IAccountRepository
    {
        private readonly Dictionary<int, Account> accounts = new();
        private int nextId = 1;
        public int AddCount { get; private set; }
        public int? LastRequestedId { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public IReadOnlyCollection<Account> Stored => accounts.Values;

        public void Seed(Account account)
        {
            accounts.Add(account.AccountId, account);
            nextId = Math.Max(nextId, account.AccountId + 1);
        }

        public Task AddAsync(Account account, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddCount++;
            account.AccountId = nextId++; // Mimics identity assignment; Oracle not involved.
            accounts.Add(account.AccountId, account);
            return Task.CompletedTask;
        }

        public Task<Account?> GetByIdAsync(int accountId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequestedId = accountId;
            LastToken = cancellationToken;
            accounts.TryGetValue(accountId, out var account);
            return Task.FromResult(account);
        }
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int SaveCount { get; private set; }
        public int TransactionCount { get; private set; }
        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            SaveCount++;
            return Task.FromResult(1);
        }

        public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            TransactionCount++;
            // Fake transaction calls work without Oracle; suitable for successful writes
            // and validation failures before modification, not commit/rollback fault injection.
            return await work(ct);
        }
    }
}
