using System.ComponentModel.DataAnnotations;
namespace TradingEngine.Api.DTOs.Account
{
    public class WithdrawRequestDto
    {
        [Range(typeof(decimal), "0.01", "9999999999999999")]
        public decimal Amount { get; set; }
    }
}