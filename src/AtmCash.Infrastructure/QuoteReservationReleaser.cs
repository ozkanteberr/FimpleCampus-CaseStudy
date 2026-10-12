using AtmCash.Domain;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Infrastructure;

internal static class QuoteReservationReleaser
{
    public static async Task Release(CashDbContext db, WithdrawalQuote quote, DateTimeOffset now,
        CancellationToken ct)
    {
        var cassetteIds = quote.Reservations.Select(x => x.CassetteId).Distinct().ToArray();
        var stocks = await db.Stocks.Where(x => cassetteIds.Contains(x.CassetteId)).ToListAsync(ct);
        foreach (var reservation in quote.Reservations)
        {
            var stock = stocks.Single(x => x.CassetteId == reservation.CassetteId
                && x.Currency == reservation.Currency && x.Denomination == reservation.Denomination);
            CashRules.Require(stock.Reserved >= reservation.Count,
                "RESERVATION_MISMATCH", "Cassette stock does not match the quote reservation.", 409);
            stock.Reserved -= reservation.Count;
            db.Movements.Add(new Movement
            {
                CassetteId = reservation.CassetteId, Currency = reservation.Currency,
                Denomination = reservation.Denomination, Type = MovementType.RESERVATION_RELEASE,
                ReservedDelta = -reservation.Count, OperationId = quote.Id,
                User = "system", CreatedAt = now
            });
        }
        if (quote.CardFingerprint is not null && quote.ReservedLimitAmount > 0)
        {
            var usage = await db.CardDailyUsages.SingleAsync(x => x.CardFingerprint == quote.CardFingerprint
                && x.Currency == quote.Currency && x.UtcDate == CardDailyLimitRules.UtcDate(quote.CreatedAt), ct);
            CardDailyLimitRules.Release(usage, quote.ReservedLimitAmount);
        }
    }
}
