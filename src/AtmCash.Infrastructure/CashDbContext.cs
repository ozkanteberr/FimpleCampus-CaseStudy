using System.Text.Json;
using AtmCash.Domain;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Infrastructure;

public sealed class CashDbContext(DbContextOptions<CashDbContext> options) : DbContext(options)
{
    public DbSet<Atm> Atms => Set<Atm>();
    public DbSet<Cassette> Cassettes => Set<Cassette>();
    public DbSet<CassetteStock> Stocks => Set<CassetteStock>();
    public DbSet<Movement> Movements => Set<Movement>();
    public DbSet<WithdrawalQuote> WithdrawalQuotes => Set<WithdrawalQuote>();
    public DbSet<WithdrawalAlternative> WithdrawalAlternatives => Set<WithdrawalAlternative>();
    public DbSet<WithdrawalAllocation> WithdrawalAllocations => Set<WithdrawalAllocation>();
    public DbSet<WithdrawalReservation> WithdrawalReservations => Set<WithdrawalReservation>();
    public DbSet<WithdrawalConfirmation> WithdrawalConfirmations => Set<WithdrawalConfirmation>();
    public DbSet<CardDailyUsage> CardDailyUsages => Set<CardDailyUsage>();
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();
    public DbSet<Reconciliation> Reconciliations => Set<Reconciliation>();
    public DbSet<ReconciliationLine> ReconciliationLines => Set<ReconciliationLine>();
    public DbSet<OutboxEvent> Outbox => Set<OutboxEvent>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Atm>().Property(x => x.Name).HasMaxLength(100);
        model.Entity<Atm>().Property(x => x.State).HasConversion<string>();
        model.Entity<Atm>().HasMany(x => x.Cassettes).WithOne().HasForeignKey(x => x.AtmId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Cassette>().HasIndex(x => new { x.AtmId, x.Number }).IsUnique();
        model.Entity<Cassette>().Property(x => x.Kind).HasConversion<string>();
        model.Entity<Cassette>().Property(x => x.Currency).HasConversion<string>();
        model.Entity<Cassette>().HasMany(x => x.Stocks).WithOne().HasForeignKey(x => x.CassetteId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Cassette>().ToTable(t => t.HasCheckConstraint("CK_Cassette_Number", "\"Number\" BETWEEN 1 AND 5"));
        model.Entity<CassetteStock>().HasKey(x => new { x.CassetteId, x.Currency, x.Denomination });
        model.Entity<CassetteStock>().Property(x => x.Currency).HasConversion<string>();
        model.Entity<CassetteStock>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_Stock_Count", "\"Count\" BETWEEN 0 AND 100 AND \"Reserved\" BETWEEN 0 AND \"Count\"");
            t.HasCheckConstraint("CK_Stock_Note", "\"Currency\" IN ('TRY','USD','EUR') AND \"Denomination\" IN (20,50,100,500)");
        });
        model.Entity<Movement>().Property(x => x.Type).HasConversion<string>();
        model.Entity<Movement>().Property(x => x.Currency).HasConversion<string>();
        model.Entity<Movement>().Property(x => x.User).HasMaxLength(100);
        model.Entity<Movement>().HasIndex(x => new { x.CassetteId, x.CreatedAt });
        model.Entity<Movement>().HasOne<Cassette>().WithMany().HasForeignKey(x => x.CassetteId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<WithdrawalQuote>().Property(x => x.Currency).HasConversion<string>();
        model.Entity<WithdrawalQuote>().Property(x => x.Status).HasConversion<string>();
        model.Entity<WithdrawalQuote>().Property(x => x.CardFingerprint).HasMaxLength(64).IsUnicode(false);
        model.Entity<WithdrawalQuote>().HasOne<Atm>().WithMany().HasForeignKey(x => x.AtmId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<WithdrawalQuote>().HasIndex(x => new { x.Status, x.ExpiresAt });
        model.Entity<WithdrawalQuote>().HasIndex(x => new { x.CardFingerprint, x.Status });
        model.Entity<WithdrawalQuote>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_WithdrawalQuote_Amount", "\"RequestedAmount\" > 0");
            t.HasCheckConstraint("CK_WithdrawalQuote_Expiry", "\"ExpiresAt\" > \"CreatedAt\"");
            t.HasCheckConstraint("CK_WithdrawalQuote_CardFingerprint",
                "\"CardFingerprint\" IS NULL OR \"CardFingerprint\" ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("CK_WithdrawalQuote_LimitReservation",
                "\"ReservedLimitAmount\" >= 0 AND (\"CardFingerprint\" IS NOT NULL OR \"ReservedLimitAmount\" = 0)");
            t.HasCheckConstraint("CK_WithdrawalQuote_Confirmation",
                "\"Status\" <> 'Confirmed' OR (\"SelectedAlternativeId\" IS NOT NULL AND \"ConfirmedAt\" IS NOT NULL)");
        });
        model.Entity<WithdrawalAlternative>().Property(x => x.Kind).HasConversion<string>();
        model.Entity<WithdrawalAlternative>().HasOne<WithdrawalQuote>().WithMany(x => x.Alternatives)
            .HasForeignKey(x => x.QuoteId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<WithdrawalAlternative>().HasIndex(x => new { x.QuoteId, x.Kind }).IsUnique();
        model.Entity<WithdrawalAlternative>().ToTable(t =>
            t.HasCheckConstraint("CK_WithdrawalAlternative_Amount", "\"Amount\" > 0"));
        model.Entity<WithdrawalAllocation>().HasKey(x => new { x.AlternativeId, x.CassetteId, x.Currency, x.Denomination });
        model.Entity<WithdrawalAllocation>().Property(x => x.Currency).HasConversion<string>();
        model.Entity<WithdrawalAllocation>().HasOne<WithdrawalAlternative>().WithMany(x => x.Allocations)
            .HasForeignKey(x => x.AlternativeId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<WithdrawalAllocation>().HasOne<Cassette>().WithMany()
            .HasForeignKey(x => x.CassetteId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<WithdrawalAllocation>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_WithdrawalAllocation_Count", "\"Count\" BETWEEN 1 AND 100");
            t.HasCheckConstraint("CK_WithdrawalAllocation_Note", "\"Currency\" IN ('TRY','USD','EUR') AND \"Denomination\" IN (20,50,100,500)");
        });
        model.Entity<WithdrawalReservation>().HasKey(x => new { x.QuoteId, x.CassetteId, x.Currency, x.Denomination });
        model.Entity<WithdrawalReservation>().Property(x => x.Currency).HasConversion<string>();
        model.Entity<WithdrawalReservation>().HasOne<WithdrawalQuote>().WithMany(x => x.Reservations)
            .HasForeignKey(x => x.QuoteId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<WithdrawalReservation>().HasOne<Cassette>().WithMany()
            .HasForeignKey(x => x.CassetteId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<WithdrawalReservation>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_WithdrawalReservation_Count", "\"Count\" BETWEEN 1 AND 100");
            t.HasCheckConstraint("CK_WithdrawalReservation_Note", "\"Currency\" IN ('TRY','USD','EUR') AND \"Denomination\" IN (20,50,100,500)");
        });
        model.Entity<WithdrawalConfirmation>().HasKey(x => x.IdempotencyKey);
        model.Entity<WithdrawalConfirmation>().Property(x => x.IdempotencyKey).HasMaxLength(128);
        model.Entity<WithdrawalConfirmation>().Property(x => x.ResponseJson).HasColumnType("jsonb");
        model.Entity<WithdrawalConfirmation>().HasOne<WithdrawalQuote>().WithMany()
            .HasForeignKey(x => x.QuoteId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<CardDailyUsage>().HasKey(x => new { x.CardFingerprint, x.Currency, x.UtcDate });
        model.Entity<CardDailyUsage>().Property(x => x.CardFingerprint).HasMaxLength(64).IsUnicode(false);
        model.Entity<CardDailyUsage>().Property(x => x.Currency).HasConversion<string>();
        model.Entity<CardDailyUsage>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_CardDailyUsage_Fingerprint", "\"CardFingerprint\" ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("CK_CardDailyUsage_Balance",
                "\"UsedAmount\" >= 0 AND \"ReservedAmount\" >= 0 AND \"UsedAmount\" + \"ReservedAmount\" <= CASE \"Currency\" WHEN 'TRY' THEN 20000 WHEN 'USD' THEN 1000 WHEN 'EUR' THEN 1000 ELSE -1 END");
        });
        model.Entity<ExchangeRate>().HasKey(x => x.Currency);
        model.Entity<ExchangeRate>().Property(x => x.Currency).HasConversion<string>();
        model.Entity<ExchangeRate>().Property(x => x.TryRate).HasPrecision(18, 6);
        // Illustrative case-study rates, not market quotations.
        model.Entity<ExchangeRate>().HasData(
            new ExchangeRate { Currency = Currency.TRY, TryRate = 1 },
            new ExchangeRate { Currency = Currency.USD, TryRate = 40 },
            new ExchangeRate { Currency = Currency.EUR, TryRate = 45 });
        model.Entity<Reconciliation>().Property(x => x.CreatedBy).HasMaxLength(100);
        model.Entity<Reconciliation>().Property(x => x.ApprovedBy).HasMaxLength(100);
        model.Entity<Reconciliation>().Property(x => x.Status).HasConversion<string>();
        model.Entity<Reconciliation>().Property(x => x.TotalAbsoluteTryDifference).HasPrecision(20, 6);
        model.Entity<Reconciliation>().Property(x => x.PhysicalCountsJson).HasColumnType("jsonb");
        model.Entity<Reconciliation>().HasOne<Atm>().WithMany()
            .HasForeignKey(x => x.AtmId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Reconciliation>().HasIndex(x => new { x.AtmId, x.Status });
        model.Entity<Reconciliation>().ToTable(t => t.HasCheckConstraint(
            "CK_Reconciliation_Total", "\"TotalAbsoluteTryDifference\" >= 0"));
        model.Entity<ReconciliationLine>().HasKey(x => new
        {
            x.ReconciliationId, x.CassetteId, x.Currency, x.Denomination
        });
        model.Entity<ReconciliationLine>().Property(x => x.Currency).HasConversion<string>();
        model.Entity<ReconciliationLine>().Property(x => x.TryRateSnapshot).HasPrecision(18, 6);
        model.Entity<ReconciliationLine>().Property(x => x.AbsoluteTryDifference).HasPrecision(20, 6);
        model.Entity<ReconciliationLine>().HasOne<Reconciliation>().WithMany(x => x.Lines)
            .HasForeignKey(x => x.ReconciliationId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ReconciliationLine>().HasOne<Cassette>().WithMany()
            .HasForeignKey(x => x.CassetteId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ReconciliationLine>().ToTable(t => t.HasCheckConstraint(
            "CK_ReconciliationLine_Counts", "\"ExpectedCount\" BETWEEN 0 AND 100 AND \"ActualCount\" BETWEEN 0 AND 100 AND \"DeltaCount\" = \"ActualCount\" - \"ExpectedCount\" AND \"TryRateSnapshot\" > 0 AND \"AbsoluteTryDifference\" >= 0"));
        model.Entity<OutboxEvent>().HasIndex(x => x.SentAt);
        model.Entity<OutboxEvent>().Property(x => x.Payload).HasColumnType("jsonb");
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (ChangeTracker.Entries<Movement>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Movement records are immutable.");
        var changedStocks = ChangeTracker.Entries<CassetteStock>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified)
            .ToArray();
        var cassetteIds = changedStocks.Select(x => x.Entity.CassetteId).Distinct().ToArray();
        if (cassetteIds.Length > 0)
        {
            var cassettes = await Cassettes.AsNoTracking()
                .Where(x => cassetteIds.Contains(x.Id))
                .Select(x => new { x.Id, x.AtmId, x.Number, x.Kind, x.Currency, x.Denomination })
                .ToDictionaryAsync(x => x.Id, cancellationToken);
            foreach (var entry in changedStocks)
            {
                if (!cassettes.TryGetValue(entry.Entity.CassetteId, out var cassette)
                    || cassette.Kind != CassetteKind.Recycling) continue;
                var before = entry.State == EntityState.Added ? 0
                    : entry.OriginalValues.GetValue<int>(nameof(CassetteStock.Count))
                      - entry.OriginalValues.GetValue<int>(nameof(CassetteStock.Reserved));
                var after = entry.Entity.Count - entry.Entity.Reserved;
                if ((before >= 10 && after < 10)
                    || (entry.State == EntityState.Added && after is > 0 and < 10))
                    Outbox.Add(new OutboxEvent
                    {
                        AtmId = cassette.AtmId,
                        CassetteId = cassette.Id,
                        Payload = JsonSerializer.Serialize(new
                        {
                            atmId = cassette.AtmId, cassetteId = cassette.Id,
                            cassetteNumber = cassette.Number, currency = cassette.Currency,
                            denomination = cassette.Denomination, availableCount = after,
                            capacity = CashRules.Capacity
                        }),
                        CreatedAt = DateTimeOffset.UtcNow
                    });
            }
        }
        return await base.SaveChangesAsync(cancellationToken);
    }
}
