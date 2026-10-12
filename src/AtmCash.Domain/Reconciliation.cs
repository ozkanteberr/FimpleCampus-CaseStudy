namespace AtmCash.Domain;

public enum ReconciliationStatus { Completed, PendingApproval, Approved }

public sealed class Reconciliation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AtmId { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public ReconciliationStatus Status { get; set; }
    public decimal TotalAbsoluteTryDifference { get; set; }
    public string PhysicalCountsJson { get; set; } = "";
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public List<ReconciliationLine> Lines { get; set; } = [];
}

public sealed class ReconciliationLine
{
    public Guid ReconciliationId { get; set; }
    public Guid CassetteId { get; set; }
    public Currency Currency { get; set; }
    public int Denomination { get; set; }
    public int ExpectedCount { get; set; }
    public int ActualCount { get; set; }
    public int DeltaCount { get; set; }
    public decimal TryRateSnapshot { get; set; }
    public decimal AbsoluteTryDifference { get; set; }
}
