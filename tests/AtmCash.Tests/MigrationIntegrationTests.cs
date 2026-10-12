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
            Assert.Single(await db.Database.GetAppliedMigrationsAsync());

            var atm = new Atm { Name = "Migration test" };
            var cassette = new Cassette { AtmId = atm.Id, Number = 1, Kind = CassetteKind.Recycling, Currency = Currency.TRY, Denomination = 100 };
            db.Atms.Add(atm);
            db.Cassettes.Add(cassette);
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
