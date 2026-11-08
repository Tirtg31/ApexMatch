namespace TradingEngine.Api.DTOs.Account
{
    public class AccountResponseDto
    {
        public int AccountId { get; set; }
        public string AccountName { get; set; } = string.Empty;
        public decimal CashBalance { get; set; }
        public decimal AvailableCash { get; set; }
        public decimal ReservedCash { get; set; }
        public bool IsActive { get; set; }
    }
}
