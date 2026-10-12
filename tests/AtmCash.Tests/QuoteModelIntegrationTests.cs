using AtmCash.Domain;
using AtmCash.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AtmCash.Tests;

public sealed class QuoteModelIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Existing_database_upgrades_and_persists_alternatives_with_merged_reservations()
    {
        var adminConnection = Environment.GetEnvironmentVariable("ATM_TEST_PG")
            ?? "Host=127.0.0.1;Port=55432;Database=postgres;Username=atm_app;Password=atm_local_password";
        var databaseName = "atm_quote_model_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
                await create.ExecuteNonQueryAsync();

            var testConnection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
            var options = new DbContextOptionsBuilder<CashDbContext>().UseNpgsql(testConnection).Options;
            var atmId = Guid.NewGuid();
            var fiftyCassetteId = Guid.NewGuid();
            var twentyCassetteId = Guid.NewGuid();
            await using (var db = new CashDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261012092747_InitialCashManagement");
                db.Atms.Add(new Atm { Id = atmId, Name = "Upgraded ATM" });
                db.Cassettes.AddRange(
                    new Cassette { Id = fiftyCassetteId, AtmId = atmId, Number = 1,
                        Kind = CassetteKind.Recycling, Currency = Currency.TRY, Denomination = 50 },
                    new Cassette { Id = twentyCassetteId, AtmId = atmId, Number = 2,
                        Kind = CassetteKind.Recycling, Currency = Currency.TRY, Denomination = 20 });
                await db.SaveChangesAsync();
                await db.Database.MigrateAsync();
            }

            var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var quote = new WithdrawalQuote
            {
                AtmId = atmId, Currency = Currency.TRY, RequestedAmount = 80,
                CreatedAt = now, ExpiresAt = now.AddSeconds(60),
                Alternatives =
                [
                    new WithdrawalAlternative
                    {
                        Kind = WithdrawalAlternativeKind.Lower, Amount = 70,
                        Allocations =
                        [
                            Allocation(fiftyCassetteId, 50, 1),
                            Allocation(twentyCassetteId, 20, 1)
                        ]
                    },
                    new WithdrawalAlternative
                    {
                        Kind = WithdrawalAlternativeKind.Upper, Amount = 90,
                        Allocations =
                        [
                            Allocation(fiftyCassetteId, 50, 1),
                            Allocation(twentyCassetteId, 20, 2)
                        ]
                    }
                ]
            };
            var plan = WithdrawalReservationRules.Build(quote.Currency, quote.RequestedAmount, quote.Alternatives);
            quote.Reservations = plan.Notes.Select(x => new WithdrawalReservation
            {
                CassetteId = x.CassetteId, Currency = x.Currency,
                Denomination = x.Denomination, Count = x.Count
            }).ToList();
            await using (var db = new CashDbContext(options))
            {
                db.WithdrawalQuotes.Add(quote);
                await db.SaveChangesAsync();
            }

            await using (var db = new CashDbContext(options))
            {
                Assert.Equal("Upgraded ATM", await db.Atms.Where(x => x.Id == atmId).Select(x => x.Name).SingleAsync());
                var saved = await db.WithdrawalQuotes
                    .Include(x => x.Alternatives).ThenInclude(x => x.Allocations)
                    .Include(x => x.Reservations)
                    .SingleAsync(x => x.Id == quote.Id);
                Assert.Equal(WithdrawalQuoteStatus.Active, saved.Status);
                Assert.Equal(now.AddSeconds(60), saved.ExpiresAt);
                Assert.Equal(2, saved.Alternatives.Count);
                Assert.Equal(2, saved.Reservations.Count);
                Assert.Equal(1, saved.Reservations.Single(x => x.CassetteId == fiftyCassetteId).Count);
                Assert.Equal(2, saved.Reservations.Single(x => x.CassetteId == twentyCassetteId).Count);
                Assert.Equal(90, plan.LimitAmount);
            }

            await using var invalid = new NpgsqlConnection(testConnection);
            await invalid.OpenAsync();
            await using var badCount = new NpgsqlCommand(
                "UPDATE \"WithdrawalReservations\" SET \"Count\" = 0 WHERE \"QuoteId\" = @id", invalid);
            badCount.Parameters.AddWithValue("id", quote.Id);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => badCount.ExecuteNonQueryAsync());
            Assert.Equal("23514", failure.SqlState);
        }
        finally
        {
            await using var terminate = new NpgsqlCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @name AND pid <> pg_backend_pid()", admin);
            terminate.Parameters.AddWithValue("name", databaseName);
            await terminate.ExecuteNonQueryAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\"", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static WithdrawalAllocation Allocation(Guid cassetteId, int denomination, int count) => new()
    {
        CassetteId = cassetteId, Currency = Currency.TRY, Denomination = denomination, Count = count
    };
}
