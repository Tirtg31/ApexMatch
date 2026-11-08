using Microsoft.AspNetCore.Mvc;
using System.Security.Principal;
using TradingEngine.Api.DTOs.Account;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Services;

namespace TradingEngine.Api.Controllers
{
    [ApiController]
    [Route("api/accounts")]
    public class AccountsController : ControllerBase
    {
        private readonly AccountService accountService;
        private readonly TradingCoordinator tradeCoordinator;
        public AccountsController(AccountService accountService, TradingCoordinator tradingCoordinator)
        {
            this.accountService = accountService;
            this.tradeCoordinator = tradingCoordinator;
        }
        [HttpGet("{id:int}")]
        public async Task<ActionResult<AccountResponseDto>> GetAccount(int id, CancellationToken ct)
        {
            Account? acc = await accountService.GetAccountByIdAsync(id, ct);
            if (acc == null)
                return NotFound($"Account with ID {id} not found.");
            AccountResponseDto response = new AccountResponseDto
            {
                AccountId = acc.AccountId,
                AccountName = acc.AccountName,
                CashBalance = acc.CashBalance,
                AvailableCash = acc.AvailableCash,
                ReservedCash = acc.ReservedCash,
                IsActive = acc.IsActive
            };
            return Ok(response);
        }
        [HttpPost]
        public async Task<ActionResult<AccountResponseDto>> CreateAccount([FromBody] CreateAccountRequestDto request, CancellationToken ct)
        {
            // 1. Map request DTO to domain entity.
            Account newAccount = new Account
            {
                AccountName = request.AccountName,
                CashBalance = request.InitialBalance
            };

            // 2. Call the existing business service.
            Account createdAccount = await accountService.CreateAccountAsync(newAccount, ct);

            // 3. Map domain entity to response DTO.
            AccountResponseDto response = new AccountResponseDto
            {
                AccountId = createdAccount.AccountId,
                AccountName = createdAccount.AccountName,
                CashBalance = createdAccount.CashBalance,
                AvailableCash = createdAccount.AvailableCash,
                ReservedCash = createdAccount.ReservedCash,
                IsActive = createdAccount.IsActive
            };

            // 4. Return 201 Created with a Location header.
            return CreatedAtAction(nameof(GetAccount), new { id = response.AccountId }, response);
        }
        [HttpPost("{id:int}/deposit")]
        public async Task<IActionResult>DepositAmount(int id, [FromBody] DepositRequestDto request, CancellationToken ct)
        {
            await tradeCoordinator.DepositAmountAsync(id, request.Amount, ct);
            return Ok();
        }
        [HttpPost("{id:int}/withdraw")]
        public async Task<IActionResult> WithdrawAmount(int id, [FromBody] WithdrawRequestDto request, CancellationToken ct)
        {
            await tradeCoordinator.WithdrawAmountAsync(id, request.Amount, ct);
            return Ok();
        }
    }
}
