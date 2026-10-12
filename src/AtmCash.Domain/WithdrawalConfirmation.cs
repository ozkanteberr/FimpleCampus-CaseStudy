namespace AtmCash.Domain;

public sealed class WithdrawalConfirmation
{
    public string IdempotencyKey { get; set; } = "";
    public Guid QuoteId { get; set; }
    public Guid AlternativeId { get; set; }
    public string ResponseJson { get; set; } = "";
}
