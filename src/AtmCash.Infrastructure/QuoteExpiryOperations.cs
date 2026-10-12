using AtmCash.Domain;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Infrastructure;

public sealed class QuoteExpiryOperations(CashDbContext db, TimeProvider clock)
{
    public async Task<IReadOnlyList<Guid>> FindDueIds(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var todayUtc = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        return await db.WithdrawalQuotes.AsNoTracking()
            .Where(x => x.Status == WithdrawalQuoteStatus.Active
                && (x.ExpiresAt <= now || x.CreatedAt < todayUtc))
            .OrderBy(x => x.ExpiresAt).Select(x => x.Id).Take(100).ToArrayAsync(ct);
    }

    public async Task<bool> Expire(Guid quoteId, CancellationToken ct)
    {
        var identity = await db.WithdrawalQuotes.AsNoTracking().Where(x => x.Id == quoteId)
            .Select(x => new { x.AtmId, x.CardFingerprint }).SingleOrDefaultAsync(ct);
        if (identity is null) return false;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (identity.CardFingerprint is not null)
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({"card:" + identity.CardFingerprint}, 0))", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"atm:" + identity.AtmId}, 0))", ct);

        var quote = await db.WithdrawalQuotes.Include(x => x.Reservations)
            .SingleAsync(x => x.Id == quoteId, ct);
        var now = clock.GetUtcNow();
        if (quote.Status != WithdrawalQuoteStatus.Active
            || (now < quote.ExpiresAt
                && CardDailyLimitRules.UtcDate(now) == CardDailyLimitRules.UtcDate(quote.CreatedAt)))
            return false;

        await QuoteReservationReleaser.Release(db, quote, now, ct);
        quote.Status = WithdrawalQuoteStatus.Expired;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
