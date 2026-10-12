using System.Text.Json;
using AtmCash.Domain;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Infrastructure;

public sealed record QuoteAllocation(int CassetteNumber, Currency Currency, int Denomination, int Count);
public sealed record QuoteAlternativeResult(Guid Id, WithdrawalAlternativeKind Kind, int Amount,
    IReadOnlyList<QuoteAllocation> Notes);
public sealed record QuoteResult(Guid Id, Guid AtmId, Currency Currency, int RequestedAmount,
    DateTimeOffset ExpiresAt, IReadOnlyList<QuoteAlternativeResult> Alternatives);
public sealed record QuoteConfirmationResult(Guid QuoteId, Guid AlternativeId, Currency Currency,
    int Amount, DateTimeOffset ConfirmedAt, IReadOnlyList<QuoteAllocation> Notes);
public sealed record QuoteCancellationResult(Guid QuoteId, WithdrawalQuoteStatus Status);

public sealed class QuoteOperations(CashDbContext db, TimeProvider clock, CardIdentity cards, NoteSelector selector)
{
    public async Task<QuoteResult> Create(Guid atmId, string cardNumber, Currency currency, int requestedAmount,
        CancellationToken ct)
    {
        var cardFingerprint = cards.Fingerprint(cardNumber);
        CardDailyLimitRules.Limit(currency);
        CashRules.Require(requestedAmount > 0, "INVALID_AMOUNT", "Withdrawal amount must be positive.");
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

        var now = clock.GetUtcNow();
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

    public async Task<QuoteConfirmationResult> Confirm(Guid quoteId, Guid alternativeId, string idempotencyKey,
        CancellationToken ct)
    {
        CashRules.Require(!string.IsNullOrWhiteSpace(idempotencyKey) && idempotencyKey.Length <= 128,
            "INVALID_IDEMPOTENCY_KEY", "Idempotency-Key header (1 to 128 characters) is required.", 400);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"idempotency:" + idempotencyKey}, 0))", ct);
        var previous = await db.WithdrawalConfirmations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, ct);
        if (previous is not null)
        {
            CashRules.Require(previous.QuoteId == quoteId && previous.AlternativeId == alternativeId,
                "IDEMPOTENCY_CONFLICT", "Idempotency key was used with a different confirmation request.", 409);
            return JsonSerializer.Deserialize<QuoteConfirmationResult>(previous.ResponseJson)
                ?? throw new InvalidOperationException("Stored confirmation response is invalid.");
        }

        var identity = await db.WithdrawalQuotes.AsNoTracking().Where(x => x.Id == quoteId)
            .Select(x => new { x.AtmId, x.CardFingerprint }).SingleOrDefaultAsync(ct)
            ?? throw new BusinessException("QUOTE_NOT_FOUND", "Quote was not found.", 404);
        CashRules.Require(identity.CardFingerprint is not null, "QUOTE_NOT_CONFIRMABLE",
            "Quote has no card identity.", 409);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"card:" + identity.CardFingerprint}, 0))", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"atm:" + identity.AtmId}, 0))", ct);

        var quote = await db.WithdrawalQuotes.Include(x => x.Alternatives).ThenInclude(x => x.Allocations)
            .Include(x => x.Reservations).AsSplitQuery().SingleAsync(x => x.Id == quoteId, ct);
        CashRules.Require(quote.Status == WithdrawalQuoteStatus.Active,
            "QUOTE_NOT_ACTIVE", "Only an active quote can be confirmed.", 409);
        var now = clock.GetUtcNow();
        CashRules.Require(now < quote.ExpiresAt
            && CardDailyLimitRules.UtcDate(now) == CardDailyLimitRules.UtcDate(quote.CreatedAt),
            "QUOTE_EXPIRED", "Quote validity period has ended.", 409);
        var alternative = quote.Alternatives.SingleOrDefault(x => x.Id == alternativeId)
            ?? throw new BusinessException("ALTERNATIVE_NOT_FOUND", "Alternative does not belong to the quote.", 404);

        var atm = await db.Atms.Include(x => x.Cassettes).ThenInclude(x => x.Stocks)
            .SingleAsync(x => x.Id == quote.AtmId, ct);
        CashRules.Require(atm.State == AtmState.IN_SERVICE,
            "INVALID_ATM_STATE", "Withdrawals require IN_SERVICE.", 409);
        var usage = await db.CardDailyUsages.SingleAsync(x => x.CardFingerprint == quote.CardFingerprint
            && x.Currency == quote.Currency && x.UtcDate == CardDailyLimitRules.UtcDate(quote.CreatedAt), ct);
        CardDailyLimitRules.Confirm(usage, quote.ReservedLimitAmount, alternative.Amount);

        var selected = alternative.Allocations.ToDictionary(
            x => (x.CassetteId, x.Currency, x.Denomination), x => x.Count);
        var reservations = quote.Reservations.ToDictionary(
            x => (x.CassetteId, x.Currency, x.Denomination));
        CashRules.Require(selected.All(x => reservations.TryGetValue(x.Key, out var reserved) && x.Value <= reserved.Count),
            "RESERVATION_MISMATCH", "Selected notes exceed the quote reservation.", 409);
        foreach (var reservation in quote.Reservations)
        {
            var cassette = atm.Cassettes.Single(x => x.Id == reservation.CassetteId);
            var stock = cassette.Stocks.Single(x => x.Currency == reservation.Currency
                && x.Denomination == reservation.Denomination);
            var count = selected.GetValueOrDefault((reservation.CassetteId, reservation.Currency, reservation.Denomination));
            CashRules.Require(stock.Reserved >= reservation.Count && stock.Count >= count,
                "RESERVATION_MISMATCH", "Cassette stock does not match the quote reservation.", 409);
            stock.Reserved -= reservation.Count;
            stock.Count -= count;
            db.Movements.Add(new Movement
            {
                CassetteId = cassette.Id, Currency = reservation.Currency,
                Denomination = reservation.Denomination, Type = MovementType.RESERVATION_RELEASE,
                ReservedDelta = -reservation.Count, OperationId = quote.Id,
                User = "system", CreatedAt = now
            });
            if (count > 0)
                db.Movements.Add(new Movement
                {
                    CassetteId = cassette.Id, Currency = reservation.Currency,
                    Denomination = reservation.Denomination, Type = MovementType.WITHDRAWAL,
                    CountDelta = -count, OperationId = quote.Id,
                    User = "system", CreatedAt = now
                });
        }
        quote.Status = WithdrawalQuoteStatus.Confirmed;
        quote.SelectedAlternativeId = alternativeId;
        quote.ConfirmedAt = now;
        var cassetteNumbers = atm.Cassettes.ToDictionary(x => x.Id, x => x.Number);
        var result = new QuoteConfirmationResult(quote.Id, alternative.Id, quote.Currency, alternative.Amount, now,
            alternative.Allocations.OrderBy(x => cassetteNumbers[x.CassetteId]).ThenByDescending(x => x.Denomination)
                .Select(x => new QuoteAllocation(cassetteNumbers[x.CassetteId], x.Currency,
                    x.Denomination, x.Count)).ToArray());
        db.WithdrawalConfirmations.Add(new WithdrawalConfirmation
        {
            IdempotencyKey = idempotencyKey, QuoteId = quote.Id, AlternativeId = alternative.Id,
            ResponseJson = JsonSerializer.Serialize(result)
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<QuoteCancellationResult> Cancel(Guid quoteId, CancellationToken ct)
    {
        var identity = await db.WithdrawalQuotes.AsNoTracking().Where(x => x.Id == quoteId)
            .Select(x => new { x.AtmId, x.CardFingerprint }).SingleOrDefaultAsync(ct)
            ?? throw new BusinessException("QUOTE_NOT_FOUND", "Quote was not found.", 404);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (identity.CardFingerprint is not null)
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({"card:" + identity.CardFingerprint}, 0))", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"atm:" + identity.AtmId}, 0))", ct);

        var quote = await db.WithdrawalQuotes.Include(x => x.Reservations)
            .SingleAsync(x => x.Id == quoteId, ct);
        CashRules.Require(quote.Status == WithdrawalQuoteStatus.Active,
            "QUOTE_NOT_ACTIVE", "Only an active quote can be cancelled.", 409);
        var now = clock.GetUtcNow();
        await QuoteReservationReleaser.Release(db, quote, now, ct);
        quote.Status = WithdrawalQuoteStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new QuoteCancellationResult(quote.Id, quote.Status);
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
