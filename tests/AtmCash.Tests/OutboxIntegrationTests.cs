using AtmCash.Domain;
using AtmCash.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AtmCash.Tests;

public sealed class OutboxIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Failed_logging_leaves_event_pending_and_next_attempt_marks_it_sent()
    {
        var adminConnection = Environment.GetEnvironmentVariable("ATM_TEST_PG")
            ?? "Host=127.0.0.1;Port=55432;Database=postgres;Username=atm_app;Password=atm_local_password";
        var databaseName = "atm_outbox_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
                await create.ExecuteNonQueryAsync();
            var testConnection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
            var options = new DbContextOptionsBuilder<CashDbContext>().UseNpgsql(testConnection).Options;
            var eventId = Guid.NewGuid();
            await using (var db = new CashDbContext(options))
            {
                await db.Database.MigrateAsync();
                db.Outbox.Add(new OutboxEvent
                {
                    Id = eventId, AtmId = Guid.NewGuid(), CassetteId = Guid.NewGuid(),
                    Payload = "{\"availableCount\":9}", CreatedAt = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync();
            }

            var logger = new ThrowOnceLogger();
            await using (var db = new CashDbContext(options))
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => new OutboxOperations(db, TimeProvider.System, logger).ProcessNext(default));
            await using (var db = new CashDbContext(options))
                Assert.Null((await db.Outbox.AsNoTracking().SingleAsync(x => x.Id == eventId)).SentAt);

            await using (var db = new CashDbContext(options))
            {
                var processor = new OutboxOperations(db, TimeProvider.System, logger);
                Assert.True(await processor.ProcessNext(default));
                Assert.False(await processor.ProcessNext(default));
            }
            await using (var db = new CashDbContext(options))
            {
                var sent = await db.Outbox.AsNoTracking().SingleAsync(x => x.Id == eventId);
                Assert.NotNull(sent.SentAt);
                Assert.Equal("LOW_CASH", sent.Type);
                Assert.Equal(2, logger.Attempts);
            }
        }
        finally
        {
            await using var terminate = new NpgsqlCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @name AND pid <> pg_backend_pid()", admin);
            terminate.Parameters.AddWithValue("name", databaseName);
            await terminate.ExecuteNonQueryAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class ThrowOnceLogger : ILogger<OutboxOperations>
    {
        public int Attempts { get; private set; }
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Attempts++;
            if (Attempts == 1) throw new InvalidOperationException("Temporary log failure");
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
