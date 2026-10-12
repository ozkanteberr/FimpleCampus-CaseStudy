using AtmCash.Domain;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Infrastructure;

public sealed record QuoteAllocation(int CassetteNumber, Currency Currency, int Denomination, int Count);
public sealed record QuoteAlternativeResult(Guid Id, WithdrawalAlternativeKind Kind, int Amount,
    IReadOnlyList<QuoteAllocation> Notes);
public sealed record QuoteResult(Guid Id, Guid AtmId, Currency Currency, int RequestedAmount,
    DateTimeOffset ExpiresAt, IReadOnlyList<QuoteAlternativeResult> Alternatives);

public sealed class QuoteOperations(CashDbContext db, TimeProvider clock, CardIdentity cards, NoteSelector selector)
{
    public async Task<QuoteResult> Create(Guid atmId, string cardNumber, Currency currency, int requestedAmount,
        CancellationToken ct)
    {
        var cardFingerprint = cards.Fingerprint(cardNumber);
        CardDailyLimitRules.Limit(currency);
        CashRules.Require(requestedAmount > 0, "INVALID_AMOUNT", "Withdrawal amount must be positive.");
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"card:" + cardFingerprint}, 0))", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"atm:" + atmId}, 0))", ct);

        var atm = await db.Atms.Include(x => x.Cassettes).ThenInclude(x => x.Stocks)
            .SingleOrDefaultAsync(x => x.Id == atmId, ct)
            ?? throw new BusinessException("ATM_NOT_FOUND", "ATM was not found.", 404);
        CashRules.Require(atm.State == AtmState.IN_SERVICE,
            "INVALID_ATM_STATE", "Withdrawals require IN_SERVICE.", 409);

        var utcDate = CardDailyLimitRules.UtcDate(now);
        var usage = await db.CardDailyUsages.SingleOrDefaultAsync(x => x.CardFingerprint == cardFingerprint
            && x.Currency == currency && x.UtcDate == utcDate, ct);
        if (usage is null)
        {
            usage = new CardDailyUsage { CardFingerprint = cardFingerprint, Currency = currency, UtcDate = utcDate };
            db.CardDailyUsages.Add(usage);
        }
        var available = atm.Cassettes.Where(x => x.Kind == CassetteKind.Recycling && x.Currency == currency)
            .SelectMany(x => x.Stocks.Where(s => s.Currency == currency && s.Denomination == x.Denomination))
            .GroupBy(x => x.Denomination)
            .ToDictionary(x => x.Key, x => x.Sum(s => s.Count - s.Reserved));
        var selection = selector.Select(available, requestedAmount, CardDailyLimitRules.Remaining(usage));
        var choices = new[]
        {
            (Kind: WithdrawalAlternativeKind.Exact, Breakdown: selection.Exact),
            (Kind: WithdrawalAlternativeKind.Lower, Breakdown: selection.Lower),
            (Kind: WithdrawalAlternativeKind.Upper, Breakdown: selection.Upper)
        };
        var alternatives = choices.Where(x => x.Breakdown is not null)
            .Select(x => Allocate(atm, currency, x.Kind, x.Breakdown!)).ToList();
        CashRules.Require(alternatives.Count > 0, "NO_PAYABLE_AMOUNT",
            "No positive amount can be dispensed with the available notes and daily limit.", 409);
        var plan = WithdrawalReservationRules.Build(currency, requestedAmount, alternatives);
        CardDailyLimitRules.Reserve(usage, plan.LimitAmount);

        var quote = new WithdrawalQuote
        {
            AtmId = atmId, CardFingerprint = cardFingerprint, Currency = currency,
            RequestedAmount = requestedAmount, ReservedLimitAmount = plan.LimitAmount,
            CreatedAt = now, ExpiresAt = now.AddSeconds(60), Alternatives = alternatives,
            Reservations = plan.Notes.Select(x => new WithdrawalReservation
            {
                CassetteId = x.CassetteId, Currency = x.Currency,
                Denomination = x.Denomination, Count = x.Count
            }).ToList()
        };
        foreach (var note in plan.Notes)
        {
            var stock = atm.Cassettes.Single(x => x.Id == note.CassetteId).Stocks
                .Single(x => x.Currency == note.Currency && x.Denomination == note.Denomination);
            CashRules.Require(stock.Count - stock.Reserved >= note.Count,
                "INSUFFICIENT_CASH", "Available cassette stock cannot cover the quote.", 409);
            stock.Reserved += note.Count;
            db.Movements.Add(new Movement
            {
                CassetteId = note.CassetteId, Currency = note.Currency, Denomination = note.Denomination,
                Type = MovementType.RESERVATION, ReservedDelta = note.Count,
                OperationId = quote.Id, User = "system", CreatedAt = now
            });
        }
        db.WithdrawalQuotes.Add(quote);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var cassetteNumbers = atm.Cassettes.ToDictionary(x => x.Id, x => x.Number);
        return new QuoteResult(quote.Id, atmId, currency, requestedAmount, quote.ExpiresAt,
            alternatives.Select(x => new QuoteAlternativeResult(x.Id, x.Kind, x.Amount,
                x.Allocations.OrderBy(y => cassetteNumbers[y.CassetteId]).ThenByDescending(y => y.Denomination)
                    .Select(y => new QuoteAllocation(cassetteNumbers[y.CassetteId], y.Currency,
                        y.Denomination, y.Count)).ToArray())).ToArray());
    }

    private static WithdrawalAlternative Allocate(Atm atm, Currency currency,
        WithdrawalAlternativeKind kind, NoteBreakdown breakdown)
    {
        var alternative = new WithdrawalAlternative { Kind = kind, Amount = breakdown.Amount };
        foreach (var (denomination, count) in breakdown.Notes.OrderByDescending(x => x.Key))
        {
            var remaining = count;
            var candidates = atm.Cassettes.Where(x => x.Kind == CassetteKind.Recycling
                    && x.Currency == currency && x.Denomination == denomination)
                .OrderByDescending(x => x.Stocks.SingleOrDefault(s => s.Currency == currency
                    && s.Denomination == denomination)?.Count ?? 0)
                .ThenBy(x => x.Number);
            foreach (var cassette in candidates)
            {
                var stock = cassette.Stocks.SingleOrDefault(x => x.Currency == currency && x.Denomination == denomination);
                var take = Math.Min(remaining, stock is null ? 0 : stock.Count - stock.Reserved);
                if (take == 0) continue;
                alternative.Allocations.Add(new WithdrawalAllocation
                {
                    CassetteId = cassette.Id, Currency = currency, Denomination = denomination, Count = take
                });
                remaining -= take;
                if (remaining == 0) break;
            }
            CashRules.Require(remaining == 0, "INSUFFICIENT_CASH", "Available cassette stock cannot cover the alternative.", 409);
        }
        return alternative;
    }
}
