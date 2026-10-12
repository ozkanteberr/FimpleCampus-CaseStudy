using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AtmCash.Tests;

public sealed class AtmMaintenanceIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Maintenance_unload_and_reconfiguration_preserve_physical_and_ledger_balances()
    {
        var adminConnection = Environment.GetEnvironmentVariable("ATM_TEST_PG")
            ?? "Host=127.0.0.1;Port=55432;Database=postgres;Username=atm_app;Password=atm_local_password";
        var databaseName = "atm_maintenance_test_" + Guid.NewGuid().ToString("N");
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
                    ["ConnectionStrings:CashDb"] = testConnection
                })));
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User", "maintenance-officer");

            var atmId = (await Read(await client.PostAsJsonAsync("/api/atms", new { name = "Maintenance ATM" })))
                .GetProperty("id").GetGuid();
            var initialConfiguration = new[]
            {
                Cassette(1, "Recycling", "TRY", 100),
                Cassette(2, "Recycling", "TRY", 50),
                Cassette(3, "Recycling", "TRY", 20),
                Cassette(4, "Recycling", "USD", 100),
                Cassette(5, "DepositOnly", null, null)
            };
            await Read(await client.PutAsJsonAsync($"/api/atms/{atmId}/configuration", new { cassettes = initialConfiguration }));
            var initialNotes = new[]
            {
                Note(1, "TRY", 100, 30), Note(2, "TRY", 50, 20),
                Note(3, "TRY", 20, 10), Note(4, "USD", 100, 15),
                Note(5, "EUR", 50, 10)
            };
            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/load", new { notes = initialNotes }));
            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { state = "IN_SERVICE" }));

            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/unload", new
            {
                notes = new[] { Note(1, "TRY", 100, 1) }
            }), HttpStatusCode.Conflict, "INVALID_ATM_STATE");
            await ExpectError(client.PutAsJsonAsync($"/api/atms/{atmId}/configuration", new
            {
                cassettes = initialConfiguration
            }), HttpStatusCode.Conflict, "INVALID_ATM_STATE");

            var maintenance = await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { state = "MAINTENANCE" }));
            Assert.Equal("MAINTENANCE", maintenance.GetProperty("state").GetString());
            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/unload", new
            {
                notes = new[] { Note(2, "TRY", 50, 1), Note(1, "TRY", 100, 31) }
            }), HttpStatusCode.Conflict, "INSUFFICIENT_CASH");
            await AssertStock(client, atmId, 1, "TRY", 100, 30);
            await AssertStock(client, atmId, 2, "TRY", 50, 20);
            await AssertConsistent(client, atmId);

            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/unload", new
            {
                notes = new[] { Note(1, "TRY", 100, 10), Note(5, "EUR", 50, 4) }
            }));
            await AssertStock(client, atmId, 1, "TRY", 100, 20);
            await AssertStock(client, atmId, 5, "EUR", 50, 6);
            await AssertConsistent(client, atmId);

            var revisedConfiguration = new[]
            {
                Cassette(1, "Recycling", "TRY", 500),
                Cassette(2, "Recycling", "TRY", 100),
                Cassette(3, "Recycling", "EUR", 20),
                Cassette(4, "Recycling", "USD", 50),
                Cassette(5, "DepositOnly", null, null)
            };
            await ExpectError(client.PutAsJsonAsync($"/api/atms/{atmId}/configuration", new
            {
                cassettes = revisedConfiguration
            }), HttpStatusCode.Conflict, "CASSETTE_NOT_EMPTY");

            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/unload", new
            {
                notes = new[]
                {
                    Note(1, "TRY", 100, 20), Note(2, "TRY", 50, 20),
                    Note(3, "TRY", 20, 10), Note(4, "USD", 100, 15),
                    Note(5, "EUR", 50, 6)
                }
            }));
            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/state", new
            {
                state = "IN_SERVICE"
            }), HttpStatusCode.Conflict, "ATM_HAS_NO_DISPENSABLE_CASH");
            await AssertConsistent(client, atmId);

            var reconfigured = await Read(await client.PutAsJsonAsync($"/api/atms/{atmId}/configuration", new
            {
                cassettes = revisedConfiguration
            }));
            Assert.Equal("MAINTENANCE", reconfigured.GetProperty("state").GetString());
            var emptyReport = await Read(await client.GetAsync($"/api/atms/{atmId}/report"));
            Assert.All(emptyReport.GetProperty("cassettes").EnumerateArray().ToArray(), cassette =>
            {
                Assert.Equal(0, cassette.GetProperty("count").GetInt32());
                Assert.Equal(0, cassette.GetProperty("notes").GetArrayLength());
            });
            await AssertConsistent(client, atmId);

            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/load", new
            {
                notes = new[] { Note(1, "TRY", 500, 5), Note(3, "EUR", 20, 5) }
            }));
            var resumed = await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { state = "IN_SERVICE" }));
            Assert.Equal("IN_SERVICE", resumed.GetProperty("state").GetString());
            await AssertStock(client, atmId, 1, "TRY", 500, 5);
            await AssertStock(client, atmId, 3, "EUR", 20, 5);
            await AssertConsistent(client, atmId);
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

    private static async Task AssertStock(HttpClient client, Guid atmId, int cassetteNumber,
        string currency, int denomination, int count)
    {
        var report = await Read(await client.GetAsync($"/api/atms/{atmId}/report"));
        var cassette = report.GetProperty("cassettes").EnumerateArray()
            .Single(x => x.GetProperty("number").GetInt32() == cassetteNumber);
        var stock = cassette.GetProperty("notes").EnumerateArray()
            .Single(x => x.GetProperty("currency").GetString() == currency
                && x.GetProperty("denomination").GetInt32() == denomination);
        Assert.Equal(count, stock.GetProperty("count").GetInt32());
        Assert.Equal(count, stock.GetProperty("available").GetInt32());
    }

    private static async Task AssertConsistent(HttpClient client, Guid atmId)
    {
        var result = await Read(await client.GetAsync($"/api/atms/{atmId}/consistency"));
        Assert.True(result.GetProperty("consistent").GetBoolean());
        Assert.Empty(result.GetProperty("differences").EnumerateArray().ToArray());
    }
}
