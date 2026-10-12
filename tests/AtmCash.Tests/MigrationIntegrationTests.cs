using AtmCash.Domain;
using AtmCash.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AtmCash.Tests;

public sealed class MigrationIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Initial_migration_creates_schema_seeds_rates_and_protects_movements()
    {
        // The connection must point to an existing administrative database, such as postgres.
        // The user must be able to create and drop a database for this isolated test.
        var adminConnection = Environment.GetEnvironmentVariable("ATM_TEST_PG")
            ?? "Host=127.0.0.1;Port=55432;Database=postgres;Username=atm_app;Password=atm_local_password";
        var databaseName = "atm_migration_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
                await create.ExecuteNonQueryAsync();

            var testConnection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
            var options = new DbContextOptionsBuilder<CashDbContext>().UseNpgsql(testConnection).Options;
            await using var db = new CashDbContext(options);
            await db.Database.MigrateAsync();

            Assert.Equal(1, await db.ExchangeRates.Where(x => x.Currency == Currency.TRY).Select(x => x.TryRate).SingleAsync());
            Assert.Equal(40, await db.ExchangeRates.Where(x => x.Currency == Currency.USD).Select(x => x.TryRate).SingleAsync());
            Assert.Equal(45, await db.ExchangeRates.Where(x => x.Currency == Currency.EUR).Select(x => x.TryRate).SingleAsync());
            var appliedMigrations = await db.Database.GetAppliedMigrationsAsync();
            Assert.Contains(appliedMigrations, x => x.EndsWith("_InitialCashManagement"));
            Assert.Contains(appliedMigrations, x => x.EndsWith("_QuoteReservations"));
            Assert.Contains(appliedMigrations, x => x.EndsWith("_CardDailyLimits"));
            Assert.Contains(appliedMigrations, x => x.EndsWith("_QuoteConfirmation"));
            Assert.Contains(appliedMigrations, x => x.EndsWith("_ConfirmationIdempotency"));

            var atm = new Atm { Name = "Migration test" };
            var cassette = new Cassette { AtmId = atm.Id, Number = 1, Kind = CassetteKind.Recycling, Currency = Currency.TRY, Denomination = 100 };
            db.Atms.Add(atm);
            db.Cassettes.Add(cassette);
            db.Stocks.Add(new CassetteStock
            {
                CassetteId = cassette.Id, Currency = Currency.TRY, Denomination = 100, Count = 1
            });
            await db.SaveChangesAsync();

            var movementId = Guid.NewGuid();
            db.Movements.Add(new Movement
            {
                Id = movementId, CassetteId = cassette.Id, Currency = Currency.TRY,
                Denomination = 100, Type = MovementType.LOAD, CountDelta = 1,
                OperationId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();

            await using var client = new NpgsqlConnection(testConnection);
            await client.OpenAsync();
            await using var update = new NpgsqlCommand("UPDATE \"Movements\" SET \"CountDelta\" = 2 WHERE \"Id\" = @id", client);
            update.Parameters.AddWithValue("id", movementId);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
            Assert.Equal("23514", failure.SqlState);

            await using var delete = new NpgsqlCommand("DELETE FROM \"Movements\" WHERE \"Id\" = @id", client);
            delete.Parameters.AddWithValue("id", movementId);
            failure = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
            Assert.Equal("23514", failure.SqlState);

            await using var truncate = new NpgsqlCommand("TRUNCATE TABLE \"Movements\"", client);
            failure = await Assert.ThrowsAsync<PostgresException>(() => truncate.ExecuteNonQueryAsync());
            Assert.Equal("23514", failure.SqlState);
            Assert.Equal(1, await db.Movements.CountAsync());

            await using (var checkDb = new CashDbContext(options))
            {
                var consistent = await new AtmOperations(checkDb, TimeProvider.System).CheckConsistency(atm.Id, default);
                Assert.True(consistent.Consistent);
                Assert.Empty(consistent.Differences);
            }

            await using var corrupt = new NpgsqlCommand(
                "UPDATE \"Stocks\" SET \"Count\" = 2, \"Reserved\" = 1 WHERE \"CassetteId\" = @id", client);
            corrupt.Parameters.AddWithValue("id", cassette.Id);
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
            await using (var checkDb = new CashDbContext(options))
            {
                var inconsistent = await new AtmOperations(checkDb, TimeProvider.System).CheckConsistency(atm.Id, default);
                Assert.False(inconsistent.Consistent);
                var difference = Assert.Single(inconsistent.Differences);
                Assert.Equal(1, difference.CassetteNumber);
                Assert.Equal(Currency.TRY, difference.Currency);
                Assert.Equal(100, difference.Denomination);
                Assert.Equal(2, difference.StoredCount);
                Assert.Equal(1, difference.LedgerCount);
                Assert.Equal(1, difference.StoredReserved);
                Assert.Equal(0, difference.LedgerReserved);
            }

            await using var missingStock = new NpgsqlCommand(
                "DELETE FROM \"Stocks\" WHERE \"CassetteId\" = @id", client);
            missingStock.Parameters.AddWithValue("id", cassette.Id);
            Assert.Equal(1, await missingStock.ExecuteNonQueryAsync());
            await using (var checkDb = new CashDbContext(options))
            {
                var inconsistent = await new AtmOperations(checkDb, TimeProvider.System).CheckConsistency(atm.Id, default);
                Assert.False(inconsistent.Consistent);
                var difference = Assert.Single(inconsistent.Differences);
                Assert.Equal(0, difference.StoredCount);
                Assert.Equal(1, difference.LedgerCount);
            }
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
