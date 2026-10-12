using AtmCash.Domain;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Infrastructure;

public sealed record DepositEntry(Currency Currency, int Denomination, int Count);
public sealed record DepositAllocation(int CassetteNumber, Currency Currency, int Denomination, int Count);
public sealed record DepositReturn(Currency Currency, int Denomination, int Count);
public sealed record DepositResult(Guid OperationId, int AcceptedCount, int ReturnedCount,
    int ReturnedUnrecognisedCount, IReadOnlyList<DepositAllocation> Allocations,
    IReadOnlyList<DepositReturn> Returned);

public sealed class DepositOperations(CashDbContext db, TimeProvider clock)
{
    public async Task<DepositResult> Deposit(Guid atmId, IReadOnlyList<DepositEntry> notes,
        int unrecognisedCount, CancellationToken ct)
    {
        CashRules.Require(unrecognisedCount >= 0, "INVALID_COUNT", "Unrecognised count cannot be negative.");
        CashRules.Require(notes.Count > 0 || unrecognisedCount > 0,
            "EMPTY_DEPOSIT", "At least one banknote is required.");
        foreach (var note in notes)
        {
            CashRules.ValidateNote(note.Currency, note.Denomination);
            CashRules.Require(note.Count > 0, "INVALID_COUNT", "Banknote count must be positive.");
        }
        var totalInput = notes.Sum(x => (long)x.Count) + unrecognisedCount;
        CashRules.Require(totalInput <= int.MaxValue, "INVALID_COUNT", "Banknote count is too large.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"atm:" + atmId}, 0))", ct);
        var atm = await db.Atms.Include(x => x.Cassettes).ThenInclude(x => x.Stocks)
            .SingleOrDefaultAsync(x => x.Id == atmId, ct)
            ?? throw new BusinessException("ATM_NOT_FOUND", "ATM was not found.", 404);
        CashRules.Require(atm.State == AtmState.IN_SERVICE,
            "INVALID_ATM_STATE", "Deposits require IN_SERVICE.", 409);

        var operationId = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var allocations = new List<DepositAllocation>();
        var returned = new List<DepositReturn>();
        var depositOnly = atm.Cassettes.Single(x => x.Kind == CassetteKind.DepositOnly);
        foreach (var note in notes.GroupBy(x => (x.Currency, x.Denomination))
            .OrderBy(x => x.Key.Currency).ThenBy(x => x.Key.Denomination))
        {
            var remaining = note.Sum(x => x.Count);
            var recycling = atm.Cassettes.Where(x => x.Kind == CassetteKind.Recycling
                    && x.Currency == note.Key.Currency && x.Denomination == note.Key.Denomination)
                .OrderBy(x => x.Stocks.Sum(s => s.Count)).ThenBy(x => x.Number);
            foreach (var cassette in recycling)
                remaining = Place(cassette, note.Key.Currency, note.Key.Denomination,
                    remaining, operationId, now, allocations);
            remaining = Place(depositOnly, note.Key.Currency, note.Key.Denomination,
                remaining, operationId, now, allocations);
            if (remaining > 0)
                returned.Add(new DepositReturn(note.Key.Currency, note.Key.Denomination, remaining));
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var acceptedCount = allocations.Sum(x => x.Count);
        return new DepositResult(operationId, acceptedCount, (int)totalInput - acceptedCount,
            unrecognisedCount, allocations.OrderBy(x => x.CassetteNumber).ThenBy(x => x.Currency)
                .ThenBy(x => x.Denomination).ToArray(), returned);
    }

    private int Place(Cassette cassette, Currency currency, int denomination, int remaining,
        Guid operationId, DateTimeOffset now, List<DepositAllocation> allocations)
    {
        var capacity = CashRules.Capacity - cassette.Stocks.Sum(x => x.Count);
        var accepted = Math.Min(remaining, capacity);
        if (accepted <= 0) return remaining;
        var stock = cassette.Stocks.SingleOrDefault(x => x.Currency == currency && x.Denomination == denomination);
        if (stock is null)
        {
            stock = new CassetteStock { CassetteId = cassette.Id, Currency = currency, Denomination = denomination };
            cassette.Stocks.Add(stock);
        }
        stock.Count += accepted;
        db.Movements.Add(new Movement
        {
            CassetteId = cassette.Id, Currency = currency, Denomination = denomination,
            Type = MovementType.DEPOSIT, CountDelta = accepted, OperationId = operationId,
            User = "customer", CreatedAt = now
        });
        allocations.Add(new DepositAllocation(cassette.Number, currency, denomination, accepted));
        return remaining - accepted;
    }
}
