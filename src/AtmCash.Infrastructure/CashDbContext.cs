using AtmCash.Domain;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Infrastructure;

public sealed class CashDbContext(DbContextOptions<CashDbContext> options) : DbContext(options)
{
    public DbSet<Atm> Atms => Set<Atm>();
    public DbSet<Cassette> Cassettes => Set<Cassette>();
    public DbSet<CassetteStock> Stocks => Set<CassetteStock>();
    public DbSet<Movement> Movements => Set<Movement>();
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();
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
        model.Entity<ExchangeRate>().HasKey(x => x.Currency);
        model.Entity<ExchangeRate>().Property(x => x.Currency).HasConversion<string>();
        model.Entity<ExchangeRate>().Property(x => x.TryRate).HasPrecision(18, 6);
        // Illustrative case-study rates, not market quotations.
        model.Entity<ExchangeRate>().HasData(
            new ExchangeRate { Currency = Currency.TRY, TryRate = 1 },
            new ExchangeRate { Currency = Currency.USD, TryRate = 40 },
            new ExchangeRate { Currency = Currency.EUR, TryRate = 45 });
        model.Entity<OutboxEvent>().HasIndex(x => x.SentAt);
        model.Entity<OutboxEvent>().Property(x => x.Payload).HasColumnType("jsonb");
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (ChangeTracker.Entries<Movement>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Movement records are immutable.");
        return base.SaveChangesAsync(cancellationToken);
    }
}
