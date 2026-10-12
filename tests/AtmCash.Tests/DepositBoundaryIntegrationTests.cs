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

public sealed class DepositBoundaryIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Mixed_and_duplicate_notes_respect_cassette_priority_capacity_and_partial_returns()
    {
        var adminConnection = Environment.GetEnvironmentVariable("ATM_TEST_PG")
            ?? "Host=127.0.0.1;Port=55432;Database=postgres;Username=atm_app;Password=atm_local_password";
        var databaseName = "atm_deposit_boundary_test_" + Guid.NewGuid().ToString("N");
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
            var atmId = await CreateAtm(client, "Mixed deposit ATM", 90, 20, 99);

            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/deposits", new
            {
                notes = Array.Empty<object>(), unrecognisedCount = 0
            }), HttpStatusCode.UnprocessableEntity, "EMPTY_DEPOSIT");
            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/deposits", new
            {
                notes = new[] { DepositNote("TRY", 30, 1) }, unrecognisedCount = 0
            }), HttpStatusCode.UnprocessableEntity, "INVALID_NOTE");
            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/deposits", new
            {
                notes = new[] { DepositNote("TRY", 100, 0) }, unrecognisedCount = 0
            }), HttpStatusCode.UnprocessableEntity, "INVALID_COUNT");
            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/deposits", new
            {
                notes = new[] { DepositNote("TRY", 100, 1) }, unrecognisedCount = -1
            }), HttpStatusCode.UnprocessableEntity, "INVALID_COUNT");

            var deposit = await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/deposits", new
            {
                notes = new[]
                {
                    DepositNote("TRY", 100, 60), DepositNote("TRY", 100, 50),
                    DepositNote("USD", 100, 2), DepositNote("EUR", 50, 3)
                },
                unrecognisedCount = 4
            }));
            var operationId = deposit.GetProperty("operationId").GetGuid();
            Assert.Equal(93, deposit.GetProperty("acceptedCount").GetInt32());
            Assert.Equal(26, deposit.GetProperty("returnedCount").GetInt32());
            Assert.Equal(4, deposit.GetProperty("returnedUnrecognisedCount").GetInt32());
            Assert.Equal(119, deposit.GetProperty("acceptedCount").GetInt32()
                + deposit.GetProperty("returnedCount").GetInt32());
            var allocations = deposit.GetProperty("allocations").EnumerateArray().ToArray();
            Assert.Equal(4, allocations.Length);
            Assert.Equal(10, AllocationCount(allocations, 1, "TRY", 100));
            Assert.Equal(80, AllocationCount(allocations, 2, "TRY", 100));
            Assert.Equal(2, AllocationCount(allocations, 3, "USD", 100));
            Assert.Equal(1, AllocationCount(allocations, 5, "TRY", 100));
            var returned = deposit.GetProperty("returned").EnumerateArray().ToArray();
            Assert.Equal(2, returned.Length);
            Assert.Equal(19, ReturnCount(returned, "TRY", 100));
            Assert.Equal(3, ReturnCount(returned, "EUR", 50));

            var report = await Read(await client.GetAsync($"/api/atms/{atmId}/report"));
            Assert.Equal(100, report.GetProperty("cassettes")[0].GetProperty("count").GetInt32());
            Assert.Equal(100, report.GetProperty("cassettes")[1].GetProperty("count").GetInt32());
            Assert.Equal(2, report.GetProperty("cassettes")[2].GetProperty("count").GetInt32());
            Assert.Equal(100, report.GetProperty("cassettes")[4].GetProperty("count").GetInt32());
            Assert.Equal(2, report.GetProperty("cassettes")[4].GetProperty("notes").GetArrayLength());
            Assert.True((await Read(await client.GetAsync($"/api/atms/{atmId}/consistency")))
                .GetProperty("consistent").GetBoolean());

            var options = new DbContextOptionsBuilder<CashDbContext>().UseNpgsql(testConnection).Options;
            await using (var db = new CashDbContext(options))
            {
                var movements = await db.Movements.Where(x => x.OperationId == operationId).ToArrayAsync();
                Assert.Equal(4, movements.Length);
                Assert.All(movements, x => Assert.Equal(MovementType.DEPOSIT, x.Type));
                Assert.Equal(93, movements.Sum(x => x.CountDelta));
            }

            var full = await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/deposits", new
            {
                notes = new[] { DepositNote("TRY", 100, 5) }, unrecognisedCount = 2
            }));
            Assert.Equal(0, full.GetProperty("acceptedCount").GetInt32());
            Assert.Equal(7, full.GetProperty("returnedCount").GetInt32());
            Assert.Empty(full.GetProperty("allocations").EnumerateArray().ToArray());
            Assert.Equal(5, ReturnCount(full.GetProperty("returned").EnumerateArray().ToArray(), "TRY", 100));
            await using (var db = new CashDbContext(options))
                Assert.False(await db.Movements.AnyAsync(x => x.OperationId == full.GetProperty("operationId").GetGuid()));

            var unrecognisedOnly = await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/deposits", new
            {
                notes = Array.Empty<object>(), unrecognisedCount = 3
            }));
            Assert.Equal(0, unrecognisedOnly.GetProperty("acceptedCount").GetInt32());
            Assert.Equal(3, unrecognisedOnly.GetProperty("returnedCount").GetInt32());
            Assert.Equal(3, unrecognisedOnly.GetProperty("returnedUnrecognisedCount").GetInt32());
            Assert.Empty(unrecognisedOnly.GetProperty("allocations").EnumerateArray().ToArray());
            Assert.Empty(unrecognisedOnly.GetProperty("returned").EnumerateArray().ToArray());
            await using (var db = new CashDbContext(options))
                Assert.False(await db.Movements.AnyAsync(x => x.OperationId
                    == unrecognisedOnly.GetProperty("operationId").GetGuid()));

            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/deposits", new
            {
                notes = new[] { DepositNote("TRY", 100, int.MaxValue) }, unrecognisedCount = 1
            }), HttpStatusCode.UnprocessableEntity, "INVALID_COUNT");

            var tieAtmId = await CreateAtm(client, "Tie deposit ATM", 5, 5, 0);
            var tie = await Read(await client.PostAsJsonAsync($"/api/atms/{tieAtmId}/deposits", new
            {
                notes = new[] { DepositNote("TRY", 100, 1) }, unrecognisedCount = 0
            }));
            Assert.Equal(1, Assert.Single(tie.GetProperty("allocations").EnumerateArray().ToArray())
                .GetProperty("cassetteNumber").GetInt32());
            Assert.True((await Read(await client.GetAsync($"/api/atms/{tieAtmId}/consistency")))
                .GetProperty("consistent").GetBoolean());
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

    private static async Task<Guid> CreateAtm(HttpClient client, string name,
        int firstCount, int secondCount, int depositOnlyCount)
    {
        var atmId = (await Read(await client.PostAsJsonAsync("/api/atms", new { name })))
            .GetProperty("id").GetGuid();
        await Read(await client.PutAsJsonAsync($"/api/atms/{atmId}/configuration", new
        {
            cassettes = new object[]
            {
                Cassette(1, "Recycling", "TRY", 100), Cassette(2, "Recycling", "TRY", 100),
                Cassette(3, "Recycling", "USD", 100), Cassette(4, "Recycling", "EUR", 100),
                Cassette(5, "DepositOnly", null, null)
            }
        }));
        var notes = new[]
        {
            Note(1, "TRY", 100, firstCount), Note(2, "TRY", 100, secondCount),
            Note(5, "TRY", 20, depositOnlyCount)
        }.Where(x => x.Count > 0).ToArray();
        await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/load", new { notes }));
        await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { state = "IN_SERVICE" }));
        return atmId;
    }

    private static int AllocationCount(JsonElement[] allocations, int cassetteNumber,
        string currency, int denomination) => allocations.Single(x =>
            x.GetProperty("cassetteNumber").GetInt32() == cassetteNumber
            && x.GetProperty("currency").GetString() == currency
            && x.GetProperty("denomination").GetInt32() == denomination).GetProperty("count").GetInt32();

    private static int ReturnCount(JsonElement[] returned, string currency, int denomination) => returned.Single(x =>
        x.GetProperty("currency").GetString() == currency
        && x.GetProperty("denomination").GetInt32() == denomination).GetProperty("count").GetInt32();

    private sealed record InputNote(int CassetteNumber, string Currency, int Denomination, int Count);

    private static InputNote Note(
        int cassetteNumber, string currency, int denomination, int count) =>
        new(cassetteNumber, currency, denomination, count);

    private static object DepositNote(string currency, int denomination, int count) =>
        new { currency, denomination, count };

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
