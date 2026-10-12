using AtmCash.Domain;
using AtmCash.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AtmCash.Tests;

public sealed class CardDailyLimitIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Daily_usage_is_shared_across_atms_and_separated_by_currency_and_utc_day()
    {
        var adminConnection = Environment.GetEnvironmentVariable("ATM_TEST_PG")
            ?? "Host=127.0.0.1;Port=55432;Database=postgres;Username=atm_app;Password=atm_local_password";
        var databaseName = "atm_card_limit_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
                await create.ExecuteNonQueryAsync();

            var testConnection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
            var options = new DbContextOptionsBuilder<CashDbContext>().UseNpgsql(testConnection).Options;
            var firstAtm = Guid.NewGuid();
            var secondAtm = Guid.NewGuid();
            var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var fingerprint = new CardIdentity("integration-test-card-hmac-key-with-32-bytes").Fingerprint("4111111111111111");
            await using (var db = new CashDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261012103534_QuoteReservations");
                db.Atms.AddRange(new Atm { Id = firstAtm, Name = "ATM 1" }, new Atm { Id = secondAtm, Name = "ATM 2" });
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "WithdrawalQuotes" ("Id", "AtmId", "Currency", "RequestedAmount", "Status", "CreatedAt", "ExpiresAt")
                    VALUES ({Guid.NewGuid()}, {firstAtm}, {"USD"}, {100}, {"Active"}, {now}, {now.AddSeconds(60)})
                    """);
                await db.Database.MigrateAsync();
                Assert.Equal(1, await db.WithdrawalQuotes.CountAsync(x => x.CardFingerprint == null && x.ReservedLimitAmount == 0));
            }

            var utcDate = CardDailyLimitRules.UtcDate(now);
            await using (var db = new CashDbContext(options))
            {
                var usage = new CardDailyUsage { CardFingerprint = fingerprint, Currency = Currency.USD, UtcDate = utcDate };
                CardDailyLimitRules.Reserve(usage, 600);
                db.CardDailyUsages.Add(usage);
                db.WithdrawalQuotes.Add(new WithdrawalQuote
                {
                    AtmId = firstAtm, CardFingerprint = fingerprint, Currency = Currency.USD,
                    RequestedAmount = 600, ReservedLimitAmount = 600,
                    CreatedAt = now, ExpiresAt = now.AddSeconds(60)
                });
                await db.SaveChangesAsync();
            }

            await using (var db = new CashDbContext(options))
            {
                var usage = await db.CardDailyUsages.SingleAsync(x => x.CardFingerprint == fingerprint
                    && x.Currency == Currency.USD && x.UtcDate == utcDate);
                Assert.Equal(400, CardDailyLimitRules.Remaining(usage));
                CardDailyLimitRules.Reserve(usage, 400);
                db.WithdrawalQuotes.Add(new WithdrawalQuote
                {
                    AtmId = secondAtm, CardFingerprint = fingerprint, Currency = Currency.USD,
                    RequestedAmount = 400, ReservedLimitAmount = 400,
                    CreatedAt = now, ExpiresAt = now.AddSeconds(60)
                });
                await db.SaveChangesAsync();
            }

            await using (var db = new CashDbContext(options))
            {
                var usage = await db.CardDailyUsages.SingleAsync(x => x.CardFingerprint == fingerprint
                    && x.Currency == Currency.USD && x.UtcDate == utcDate);
                Assert.Equal(1_000, usage.ReservedAmount);
                Assert.Equal(0, CardDailyLimitRules.Remaining(usage));
                Assert.Equal("DAILY_LIMIT_EXCEEDED",
                    Assert.Throws<BusinessException>(() => CardDailyLimitRules.Reserve(usage, 1)).Code);
                Assert.Equal(2, await db.WithdrawalQuotes.CountAsync(x => x.CardFingerprint == fingerprint));
                Assert.Equal(2, await db.WithdrawalQuotes.Where(x => x.CardFingerprint == fingerprint)
                    .Select(x => x.AtmId).Distinct().CountAsync());

                var tryUsage = new CardDailyUsage { CardFingerprint = fingerprint, Currency = Currency.TRY, UtcDate = utcDate };
                CardDailyLimitRules.Reserve(tryUsage, 20_000);
                var nextDayUsage = new CardDailyUsage { CardFingerprint = fingerprint, Currency = Currency.USD,
                    UtcDate = utcDate.AddDays(1) };
                CardDailyLimitRules.Reserve(nextDayUsage, 1_000);
                db.CardDailyUsages.AddRange(tryUsage, nextDayUsage);
                await db.SaveChangesAsync();
                Assert.Equal(3, await db.CardDailyUsages.CountAsync(x => x.CardFingerprint == fingerprint));
            }

            await using var client = new NpgsqlConnection(testConnection);
            await client.OpenAsync();
            await using var overLimit = new NpgsqlCommand(
                "UPDATE \"CardDailyUsages\" SET \"ReservedAmount\" = 1001 WHERE \"CardFingerprint\" = @fingerprint AND \"Currency\" = 'USD' AND \"UtcDate\" = @date", client);
            overLimit.Parameters.AddWithValue("fingerprint", fingerprint);
            overLimit.Parameters.AddWithValue("date", utcDate);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => overLimit.ExecuteNonQueryAsync());
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
}
