using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AtmCash.Infrastructure;

public sealed class OutboxOperations(CashDbContext db, TimeProvider clock, ILogger<OutboxOperations> logger)
{
    public async Task<bool> ProcessNext(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var pending = await db.Outbox.FromSqlRaw(
                "SELECT * FROM \"Outbox\" WHERE \"SentAt\" IS NULL ORDER BY \"CreatedAt\", \"Id\" FOR UPDATE SKIP LOCKED LIMIT 1")
            .ToListAsync(ct);
        var item = pending.SingleOrDefault();
        if (item is null) return false;
        logger.LogInformation("Outbox event {EventId} type {EventType} ATM {AtmId} cassette {CassetteId}: {Payload}",
            item.Id, item.Type, item.AtmId, item.CassetteId, item.Payload);
        item.SentAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
