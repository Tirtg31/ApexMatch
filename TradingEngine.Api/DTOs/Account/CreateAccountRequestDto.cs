using System.ComponentModel.DataAnnotations;

namespace TradingEngine.Api.DTOs.Account
{
    public class CreateAccountRequestDto
    {
        [Required]
        [StringLength(100, MinimumLength = 1)]
        public string AccountName { get; set; } = string.Empty;

        [Range(0, double.MaxValue)]
        public decimal InitialBalance { get; set; }
    }
}
