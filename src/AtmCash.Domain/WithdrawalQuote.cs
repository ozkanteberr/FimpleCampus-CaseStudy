namespace AtmCash.Domain;

public enum WithdrawalQuoteStatus { Active, Confirmed, Cancelled, Expired }
public enum WithdrawalAlternativeKind { Exact, Lower, Upper }

public sealed class WithdrawalQuote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AtmId { get; set; }
    public Currency Currency { get; set; }
    public int RequestedAmount { get; set; }
    public string? CardFingerprint { get; set; }
    public int ReservedLimitAmount { get; set; }
    public WithdrawalQuoteStatus Status { get; set; } = WithdrawalQuoteStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public List<WithdrawalAlternative> Alternatives { get; set; } = [];
    public List<WithdrawalReservation> Reservations { get; set; } = [];
}

public sealed class WithdrawalAlternative
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid QuoteId { get; set; }
    public WithdrawalAlternativeKind Kind { get; set; }
    public int Amount { get; set; }
    public List<WithdrawalAllocation> Allocations { get; set; } = [];
}

public sealed class WithdrawalAllocation
{
    public Guid AlternativeId { get; set; }
    public Guid CassetteId { get; set; }
    public Currency Currency { get; set; }
    public int Denomination { get; set; }
    public int Count { get; set; }
}

public sealed class WithdrawalReservation
{
    public Guid QuoteId { get; set; }
    public Guid CassetteId { get; set; }
    public Currency Currency { get; set; }
    public int Denomination { get; set; }
    public int Count { get; set; }
}
