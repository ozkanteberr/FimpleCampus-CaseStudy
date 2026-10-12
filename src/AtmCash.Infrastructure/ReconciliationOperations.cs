using System.Text.Json;
using AtmCash.Domain;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Infrastructure;

public sealed record PhysicalNoteCount(Currency Currency, int Denomination, int Count);
public sealed record CassettePhysicalCount(int CassetteNumber, IReadOnlyList<PhysicalNoteCount> Notes);
public sealed record ReconciliationLineResult(int CassetteNumber, Currency Currency, int Denomination,
    int ExpectedCount, int ActualCount, int DeltaCount, decimal TryRateSnapshot,
    decimal AbsoluteTryDifference);
public sealed record ReconciliationResult(Guid Id, Guid AtmId, ReconciliationStatus Status,
    decimal TotalAbsoluteTryDifference, DateTimeOffset CreatedAt,
    IReadOnlyList<ReconciliationLineResult> Lines);
public sealed record ReconciliationApprovalResult(Guid Id, Guid AtmId, ReconciliationStatus Status,
    string ApprovedBy, DateTimeOffset ApprovedAt);

public sealed class ReconciliationOperations(CashDbContext db, TimeProvider clock)
{
    public async Task<ReconciliationResult> Create(Guid atmId, IReadOnlyList<CassettePhysicalCount> counts,
        string user, CancellationToken ct)
    {
        CashRules.Require(counts.Count == 5 && counts.Select(x => x.CassetteNumber).Order()
            .SequenceEqual(new[] { 1, 2, 3, 4, 5 }),
            "INVALID_PHYSICAL_COUNT", "Physical counts for all five cassettes are required.");
        foreach (var cassette in counts)
        {
            var notes = cassette.Notes ?? throw new BusinessException("INVALID_PHYSICAL_COUNT",
                "Each cassette requires a notes list.");
            CashRules.Require(notes.Select(x => (x.Currency, x.Denomination)).Distinct().Count()
                == notes.Count, "DUPLICATE_PHYSICAL_NOTE", "Duplicate note counts are not allowed.");
            foreach (var note in notes)
            {
                CashRules.ValidateNote(note.Currency, note.Denomination);
                CashRules.Require(note.Count is >= 0 and <= CashRules.Capacity,
                    "INVALID_COUNT", "Physical note count must be from 0 to 100.");
            }
            CashRules.Require(notes.Sum(x => x.Count) <= CashRules.Capacity,
                "CASSETTE_FULL", "Cassette physical count exceeds capacity.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"atm:" + atmId}, 0))", ct);
        var atm = await db.Atms.Include(x => x.Cassettes).ThenInclude(x => x.Stocks)
            .SingleOrDefaultAsync(x => x.Id == atmId, ct)
            ?? throw new BusinessException("ATM_NOT_FOUND", "ATM was not found.", 404);
        CashRules.Require(atm.State == AtmState.MAINTENANCE,
            "INVALID_ATM_STATE", "Reconciliation requires MAINTENANCE.", 409);
        CashRules.Require(!await db.Reconciliations.AnyAsync(x => x.AtmId == atmId
            && x.Status == ReconciliationStatus.PendingApproval, ct),
            "PENDING_RECONCILIATION", "A reconciliation is awaiting approval.", 409);
        CashRules.Require(!await db.WithdrawalQuotes.AnyAsync(x => x.AtmId == atmId
            && x.Status == WithdrawalQuoteStatus.Active, ct)
            && atm.Cassettes.All(x => x.Stocks.All(s => s.Reserved == 0)),
            "ACTIVE_RESERVATIONS", "Active reservations must be released before reconciliation.", 409);
        var rates = await db.ExchangeRates.AsNoTracking().ToDictionaryAsync(x => x.Currency, x => x.TryRate, ct);
        var now = clock.GetUtcNow();
        var reconciliation = new Reconciliation
        {
            AtmId = atmId, CreatedBy = user, CreatedAt = now,
            PhysicalCountsJson = JsonSerializer.Serialize(counts.OrderBy(x => x.CassetteNumber))
        };
        var results = new List<ReconciliationLineResult>();
        foreach (var input in counts.OrderBy(x => x.CassetteNumber))
        {
            var cassette = atm.Cassettes.Single(x => x.Number == input.CassetteNumber);
            if (cassette.Kind == CassetteKind.Recycling)
                CashRules.Require(input.Notes.All(x => x.Currency == cassette.Currency
                    && x.Denomination == cassette.Denomination),
                    "CASSETTE_NOTE_MISMATCH", "Physical note does not match the recycling cassette.");
            var actual = input.Notes.ToDictionary(x => (x.Currency, x.Denomination), x => x.Count);
            var keys = cassette.Stocks.Select(x => (x.Currency, x.Denomination))
                .Concat(actual.Keys).Distinct().OrderBy(x => x.Currency).ThenBy(x => x.Denomination);
            foreach (var key in keys)
            {
                var stock = cassette.Stocks.SingleOrDefault(x => x.Currency == key.Currency
                    && x.Denomination == key.Denomination);
                var expected = stock?.Count ?? 0;
                var counted = actual.GetValueOrDefault(key);
                var delta = counted - expected;
                var rate = rates[key.Currency];
                CashRules.Require(rate > 0, "INVALID_EXCHANGE_RATE", "Exchange rate must be positive.", 409);
                var absoluteTry = Math.Abs(delta) * (decimal)key.Denomination * rate;
                reconciliation.Lines.Add(new ReconciliationLine
                {
                    CassetteId = cassette.Id, Currency = key.Currency, Denomination = key.Denomination,
                    ExpectedCount = expected, ActualCount = counted, DeltaCount = delta,
                    TryRateSnapshot = rate, AbsoluteTryDifference = absoluteTry
                });
                results.Add(new ReconciliationLineResult(cassette.Number, key.Currency, key.Denomination,
                    expected, counted, delta, rate, absoluteTry));
                reconciliation.TotalAbsoluteTryDifference += absoluteTry;
                if (delta == 0) continue;
                if (stock is null)
                {
                    stock = new CassetteStock
                    {
                        CassetteId = cassette.Id, Currency = key.Currency, Denomination = key.Denomination
                    };
                    cassette.Stocks.Add(stock);
                }
                stock.Count = counted;
                db.Movements.Add(new Movement
                {
                    CassetteId = cassette.Id, Currency = key.Currency, Denomination = key.Denomination,
                    Type = MovementType.RECONCILIATION_ADJUSTMENT, CountDelta = delta,
                    OperationId = reconciliation.Id, User = user, CreatedAt = now
                });
            }
        }
        reconciliation.Status = reconciliation.TotalAbsoluteTryDifference > 1000m
            ? ReconciliationStatus.PendingApproval : ReconciliationStatus.Completed;
        db.Reconciliations.Add(reconciliation);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new ReconciliationResult(reconciliation.Id, atmId, reconciliation.Status,
            reconciliation.TotalAbsoluteTryDifference, now, results);
    }

    public async Task<ReconciliationApprovalResult> Approve(Guid reconciliationId, string user,
        CancellationToken ct)
    {
        var atmId = await db.Reconciliations.AsNoTracking().Where(x => x.Id == reconciliationId)
            .Select(x => (Guid?)x.AtmId).SingleOrDefaultAsync(ct)
            ?? throw new BusinessException("RECONCILIATION_NOT_FOUND", "Reconciliation was not found.", 404);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"atm:" + atmId}, 0))", ct);
        var reconciliation = await db.Reconciliations.SingleAsync(x => x.Id == reconciliationId, ct);
        CashRules.Require(reconciliation.Status == ReconciliationStatus.PendingApproval,
            "RECONCILIATION_NOT_PENDING", "Only a pending reconciliation can be approved.", 409);
        CashRules.Require(!string.Equals(reconciliation.CreatedBy.Trim(), user.Trim(),
            StringComparison.OrdinalIgnoreCase),
            "SELF_APPROVAL_FORBIDDEN", "The counting user cannot approve the reconciliation.", 409);
        var atmState = await db.Atms.Where(x => x.Id == atmId).Select(x => x.State).SingleAsync(ct);
        CashRules.Require(atmState == AtmState.MAINTENANCE,
            "INVALID_ATM_STATE", "Approval requires MAINTENANCE.", 409);

        var approvedAt = clock.GetUtcNow();
        reconciliation.Status = ReconciliationStatus.Approved;
        reconciliation.ApprovedBy = user.Trim();
        reconciliation.ApprovedAt = approvedAt;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new ReconciliationApprovalResult(reconciliation.Id, atmId, reconciliation.Status,
            reconciliation.ApprovedBy, approvedAt);
    }
}
