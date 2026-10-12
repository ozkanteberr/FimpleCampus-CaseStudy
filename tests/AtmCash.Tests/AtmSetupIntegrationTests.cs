using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AtmCash.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AtmCash.Tests;

public sealed class AtmSetupIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Low_cash_event_is_persisted_with_reservation_on_threshold_crossing()
    {
        var adminConnection = Environment.GetEnvironmentVariable("ATM_TEST_PG")
            ?? "Host=127.0.0.1;Port=55432;Database=postgres;Username=atm_app;Password=atm_local_password";
        var databaseName = "atm_low_cash_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
                await create.ExecuteNonQueryAsync();
            var testConnection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:CashDb"] = testConnection,
                    ["CardIdentity:HmacKey"] = "integration-test-card-hmac-key-with-32-bytes"
                })));
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User", "setup-officer");
            var atmId = (await Read(await client.PostAsJsonAsync("/api/atms", new { name = "Low Cash ATM" })))
                .GetProperty("id").GetGuid();
            await Read(await client.PutAsJsonAsync($"/api/atms/{atmId}/configuration", new
            {
                cassettes = new[]
                {
                    Cassette(1, "Recycling", "TRY", 100), Cassette(2, "Recycling", "TRY", 20),
                    Cassette(3, "Recycling", "USD", 100), Cassette(4, "Recycling", "EUR", 100),
                    Cassette(5, "DepositOnly", null, null)
                }
            }));
            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/load",
                new { notes = new[] { Note(1, "TRY", 100, 10) } }));
            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { state = "IN_SERVICE" }));
            var options = new DbContextOptionsBuilder<CashDbContext>().UseNpgsql(testConnection).Options;
            await using var db = new CashDbContext(options);
            Assert.Empty(await db.Outbox.ToListAsync());

            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/quotes",
                new { cardNumber = "4111111111111111", currency = "TRY", amount = 0 }),
                HttpStatusCode.UnprocessableEntity, "INVALID_AMOUNT");
            Assert.Empty(await db.Outbox.ToListAsync());

            var firstQuote = await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/quotes",
                new { cardNumber = "4111111111111111", currency = "TRY", amount = 100 }));
            var cassette = (await db.Cassettes.SingleAsync(x => x.AtmId == atmId && x.Number == 1));
            var stock = await db.Stocks.SingleAsync(x => x.CassetteId == cassette.Id);
            Assert.Equal(10, stock.Count);
            Assert.Equal(1, stock.Reserved);
            var events = await db.Outbox.ToListAsync();
            var alert = Assert.Single(events);
            Assert.Equal("LOW_CASH", alert.Type);
            Assert.Null(alert.SentAt);
            Assert.Equal(atmId, alert.AtmId);
            Assert.Equal(9, JsonDocument.Parse(alert.Payload).RootElement.GetProperty("availableCount").GetInt32());

            await Read(await client.PostAsync($"/api/quotes/{firstQuote.GetProperty("id").GetGuid()}/cancel", null));
            Assert.Single(await db.Outbox.ToListAsync());
            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/quotes",
                new { cardNumber = "4111111111111111", currency = "TRY", amount = 100 }));
            Assert.Equal(2, await db.Outbox.CountAsync());
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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Atm_setup_enforces_lifecycle_capacity_and_atomic_loading()
    {
        var adminConnection = Environment.GetEnvironmentVariable("ATM_TEST_PG")
            ?? "Host=127.0.0.1;Port=55432;Database=postgres;Username=atm_app;Password=atm_local_password";
        var databaseName = "atm_setup_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
                await create.ExecuteNonQueryAsync();

            var testConnection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:CashDb"] = testConnection,
                    ["CardIdentity:HmacKey"] = "integration-test-card-hmac-key-with-32-bytes"
                })));
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User", "setup-officer");

            var created = await client.PostAsJsonAsync("/api/atms", new { name = "Test ATM" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var atmId = (await Read(created)).GetProperty("id").GetGuid();

            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { state = "IN_SERVICE" }),
                HttpStatusCode.Conflict, "INVALID_STATE_TRANSITION");
            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/load", new
            {
                notes = new[] { Note(1, "TRY", 100, 5) }
            }), HttpStatusCode.Conflict, "INVALID_ATM_STATE");

            var configuration = new[]
            {
                Cassette(1, "Recycling", "TRY", 100),
                Cassette(2, "Recycling", "TRY", 50),
                Cassette(3, "Recycling", "TRY", 20),
                Cassette(4, "Recycling", "USD", 100),
                Cassette(5, "DepositOnly", null, null)
            };
            var configured = await client.PutAsJsonAsync($"/api/atms/{atmId}/configuration", new { cassettes = configuration });
            Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
            Assert.Equal("CONFIGURED", (await Read(configured)).GetProperty("state").GetString());

            var overflow = await client.PostAsJsonAsync($"/api/atms/{atmId}/load", new
            {
                notes = new[]
                {
                    Note(1, "TRY", 100, 80),
                    Note(5, "TRY", 20, 60),
                    Note(5, "EUR", 50, 50)
                }
            });
            await ExpectError(Task.FromResult(overflow), HttpStatusCode.Conflict, "CASSETTE_FULL");

            var beforeLoad = await Read(await client.GetAsync($"/api/atms/{atmId}/report"));
            Assert.Equal("CONFIGURED", beforeLoad.GetProperty("state").GetString());
            Assert.All(beforeLoad.GetProperty("cassettes").EnumerateArray().ToArray(),
                cassette => Assert.Equal(0, cassette.GetProperty("count").GetInt32()));
            var beforeConsistency = await Read(await client.GetAsync($"/api/atms/{atmId}/consistency"));
            Assert.True(beforeConsistency.GetProperty("consistent").GetBoolean());

            var loaded = await client.PostAsJsonAsync($"/api/atms/{atmId}/load", new
            {
                notes = new[]
                {
                    Note(1, "TRY", 100, 80), Note(2, "TRY", 50, 90),
                    Note(3, "TRY", 20, 75), Note(4, "USD", 100, 60),
                    Note(5, "EUR", 50, 10)
                }
            });
            Assert.Equal(HttpStatusCode.OK, loaded.StatusCode);
            Assert.Equal("LOADED", (await Read(loaded)).GetProperty("state").GetString());
            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/load", new
            {
                notes = new[] { Note(1, "TRY", 100, 1) }
            }), HttpStatusCode.Conflict, "INVALID_ATM_STATE");

            var inService = await client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { state = "IN_SERVICE" });
            Assert.Equal(HttpStatusCode.OK, inService.StatusCode);
            Assert.Equal("IN_SERVICE", (await Read(inService)).GetProperty("state").GetString());

            var report = await Read(await client.GetAsync($"/api/atms/{atmId}/report"));
            Assert.Equal(5, report.GetProperty("cassettes").GetArrayLength());
            Assert.Equal(276500m, report.GetProperty("overallTryBalance").GetDecimal());
            Assert.Equal(80, report.GetProperty("cassettes")[0].GetProperty("available").GetInt32());
            var secondId = (await Read(await client.PostAsJsonAsync("/api/atms",
                new { name = "Second ATM" }))).GetProperty("id").GetGuid();
            await Read(await client.PutAsJsonAsync($"/api/atms/{secondId}/configuration",
                new { cassettes = configuration }));
            await Read(await client.PostAsJsonAsync($"/api/atms/{secondId}/load", new
            {
                notes = new[] { Note(3, "TRY", 20, 2), Note(4, "USD", 100, 1) }
            }));
            await Read(await client.PostAsJsonAsync($"/api/atms/{secondId}/state",
                new { state = "IN_SERVICE" }));
            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/quotes", new
            {
                cardNumber = "4111111111111111", currency = "TRY", amount = 100
            }));
            var network = await Read(await client.GetAsync("/api/report"));
            Assert.Equal(2, network.GetProperty("atms").GetArrayLength());
            Assert.Equal(280_540m, network.GetProperty("overallTryBalance").GetDecimal());
            var atmReports = network.GetProperty("atms").EnumerateArray().ToArray();
            Assert.Equal("Second ATM", atmReports[0].GetProperty("name").GetString());
            Assert.Equal(4_040m, atmReports[0].GetProperty("overallTryBalance").GetDecimal());
            Assert.Equal(276_500m, atmReports[1].GetProperty("overallTryBalance").GetDecimal());
            var tryTotal = network.GetProperty("totals").EnumerateArray()
                .Single(x => x.GetProperty("currency").GetString() == "TRY");
            Assert.Equal(100m, tryTotal.GetProperty("reserved").GetDecimal());
            Assert.Equal(14_040m, tryTotal.GetProperty("balance").GetDecimal());
            Assert.Equal(13_940m, tryTotal.GetProperty("available").GetDecimal());
            Assert.Equal(1, atmReports[1].GetProperty("cassettes")[0].GetProperty("reserved").GetInt32());
            var consistency = await Read(await client.GetAsync($"/api/atms/{atmId}/consistency"));
            Assert.True(consistency.GetProperty("consistent").GetBoolean());
            Assert.Empty(consistency.GetProperty("differences").EnumerateArray().ToArray());
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

    private static object Note(int cassetteNumber, string currency, int denomination, int count) =>
        new { cassetteNumber, currency, denomination, count };

    private static object Cassette(int number, string kind, string? currency, int? denomination) =>
        new { number, kind, currency, denomination };

    private static async Task<JsonElement> Read(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task ExpectError(Task<HttpResponseMessage> responseTask, HttpStatusCode status, string code)
    {
        using var response = await responseTask;
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }
}
