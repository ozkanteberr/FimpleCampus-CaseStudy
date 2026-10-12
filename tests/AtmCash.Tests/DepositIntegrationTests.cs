using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AtmCash.Domain;
using AtmCash.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AtmCash.Tests;

public sealed class DepositIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Deposit_fills_recycling_then_deposit_only_and_returns_the_remainder()
    {
        var adminConnection = Environment.GetEnvironmentVariable("ATM_TEST_PG")
            ?? "Host=127.0.0.1;Port=55432;Database=postgres;Username=atm_app;Password=atm_local_password";
        var databaseName = "atm_deposit_test_" + Guid.NewGuid().ToString("N");
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
            client.DefaultRequestHeaders.Add("X-User", "cash-officer");
            var atmId = (await Read(await client.PostAsJsonAsync("/api/atms", new { name = "Deposit ATM" })))
                .GetProperty("id").GetGuid();
            await Read(await client.PutAsJsonAsync($"/api/atms/{atmId}/configuration", new
            {
                cassettes = new object[]
                {
                    Cassette(1, "Recycling", "TRY", 100), Cassette(2, "Recycling", "TRY", 20),
                    Cassette(3, "Recycling", "USD", 100), Cassette(4, "Recycling", "EUR", 100),
                    Cassette(5, "DepositOnly", null, null)
                }
            }));
            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/load", new
            {
                notes = new[] { Note(1, "TRY", 100, 99), Note(5, "TRY", 20, 99) }
            }));
            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/deposits", new
            {
                notes = new[] { DepositNote("TRY", 100, 4) }, unrecognisedCount = 2
            }), HttpStatusCode.Conflict, "INVALID_ATM_STATE");
            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { state = "IN_SERVICE" }));

            var result = await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/deposits", new
            {
                notes = new[] { DepositNote("TRY", 100, 4) }, unrecognisedCount = 2
            }));
            var operationId = result.GetProperty("operationId").GetGuid();
            Assert.Equal(2, result.GetProperty("acceptedCount").GetInt32());
            Assert.Equal(4, result.GetProperty("returnedCount").GetInt32());
            Assert.Equal(2, result.GetProperty("returnedUnrecognisedCount").GetInt32());
            var allocations = result.GetProperty("allocations").EnumerateArray().ToArray();
            Assert.Equal(2, allocations.Length);
            Assert.Equal(1, allocations.Single(x => x.GetProperty("cassetteNumber").GetInt32() == 1)
                .GetProperty("count").GetInt32());
            Assert.Equal(1, allocations.Single(x => x.GetProperty("cassetteNumber").GetInt32() == 5)
                .GetProperty("count").GetInt32());
            var returned = Assert.Single(result.GetProperty("returned").EnumerateArray().ToArray());
            Assert.Equal("TRY", returned.GetProperty("currency").GetString());
            Assert.Equal(100, returned.GetProperty("denomination").GetInt32());
            Assert.Equal(2, returned.GetProperty("count").GetInt32());

            var report = await Read(await client.GetAsync($"/api/atms/{atmId}/report"));
            Assert.Equal(100, report.GetProperty("cassettes")[0].GetProperty("count").GetInt32());
            Assert.Equal(100, report.GetProperty("cassettes")[4].GetProperty("count").GetInt32());
            Assert.Equal(2, report.GetProperty("cassettes")[4].GetProperty("notes").GetArrayLength());
            Assert.True((await Read(await client.GetAsync($"/api/atms/{atmId}/consistency")))
                .GetProperty("consistent").GetBoolean());

            var options = new DbContextOptionsBuilder<CashDbContext>().UseNpgsql(testConnection).Options;
            await using var db = new CashDbContext(options);
            var movements = await db.Movements.Where(x => x.OperationId == operationId).ToArrayAsync();
            Assert.Equal(2, movements.Length);
            Assert.All(movements, x => Assert.Equal(MovementType.DEPOSIT, x.Type));
            Assert.Equal(2, movements.Sum(x => x.CountDelta));
            Assert.All(movements, x => Assert.Equal(0, x.ReservedDelta));
            Assert.Equal(0, await db.CardDailyUsages.CountAsync());
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

    private static object DepositNote(string currency, int denomination, int count) =>
        new { currency, denomination, count };

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
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }
}
