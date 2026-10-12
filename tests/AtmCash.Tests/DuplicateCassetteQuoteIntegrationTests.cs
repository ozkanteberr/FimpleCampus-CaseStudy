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

public sealed class DuplicateCassetteQuoteIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Same_note_cassettes_dispense_from_fuller_first_without_overbooking()
    {
        var adminConnection = Environment.GetEnvironmentVariable("ATM_TEST_PG")
            ?? "Host=127.0.0.1;Port=55432;Database=postgres;Username=atm_app;Password=atm_local_password";
        var databaseName = "atm_duplicate_cassette_test_" + Guid.NewGuid().ToString("N");
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

            var atmId = await CreateAtm(client, "Fuller cassette ATM", 60, 80);
            var first = await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/quotes", new
            {
                cardNumber = "4111111111111111", currency = "TRY", amount = 9000
            }));
            var firstNotes = Assert.Single(first.GetProperty("alternatives").EnumerateArray().ToArray())
                .GetProperty("notes").EnumerateArray().ToArray();
            Assert.Equal(80, Count(firstNotes, 2));
            Assert.Equal(10, Count(firstNotes, 1));

            var second = await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/quotes", new
            {
                cardNumber = "4111111111111112", currency = "TRY", amount = 5000
            }));
            var secondNotes = Assert.Single(second.GetProperty("alternatives").EnumerateArray().ToArray())
                .GetProperty("notes").EnumerateArray().ToArray();
            Assert.Equal(50, Count(secondNotes, 1));
            Assert.DoesNotContain(secondNotes, x => x.GetProperty("cassetteNumber").GetInt32() == 2);
            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/quotes", new
            {
                cardNumber = "4111111111111113", currency = "TRY", amount = 100
            }), HttpStatusCode.Conflict, "NO_PAYABLE_AMOUNT");

            var report = await Read(await client.GetAsync($"/api/atms/{atmId}/report"));
            var firstCassette = report.GetProperty("cassettes")[0];
            var secondCassette = report.GetProperty("cassettes")[1];
            Assert.Equal(60, firstCassette.GetProperty("count").GetInt32());
            Assert.Equal(60, firstCassette.GetProperty("reserved").GetInt32());
            Assert.Equal(80, secondCassette.GetProperty("count").GetInt32());
            Assert.Equal(80, secondCassette.GetProperty("reserved").GetInt32());
            Assert.Equal(0, firstCassette.GetProperty("available").GetInt32());
            Assert.Equal(0, secondCassette.GetProperty("available").GetInt32());
            Assert.All(report.GetProperty("cassettes").EnumerateArray().ToArray(), cassette =>
            {
                var count = cassette.GetProperty("count").GetInt32();
                var reserved = cassette.GetProperty("reserved").GetInt32();
                Assert.InRange(count, 0, CashRules.Capacity);
                Assert.InRange(reserved, 0, count);
            });
            Assert.True((await Read(await client.GetAsync($"/api/atms/{atmId}/consistency")))
                .GetProperty("consistent").GetBoolean());

            var tieAtmId = await CreateAtm(client, "Tie breaker ATM", 2, 2);
            var tie = await Read(await client.PostAsJsonAsync($"/api/atms/{tieAtmId}/quotes", new
            {
                cardNumber = "4111111111111114", currency = "TRY", amount = 100
            }));
            var tieNotes = Assert.Single(tie.GetProperty("alternatives").EnumerateArray().ToArray())
                .GetProperty("notes").EnumerateArray().ToArray();
            Assert.Equal(1, Count(tieNotes, 1));
            Assert.DoesNotContain(tieNotes, x => x.GetProperty("cassetteNumber").GetInt32() == 2);
            Assert.True((await Read(await client.GetAsync($"/api/atms/{tieAtmId}/consistency")))
                .GetProperty("consistent").GetBoolean());

            var options = new DbContextOptionsBuilder<CashDbContext>().UseNpgsql(testConnection).Options;
            await using var db = new CashDbContext(options);
            var cassetteIds = await db.Cassettes.Where(x => x.AtmId == atmId && (x.Number == 1 || x.Number == 2))
                .Select(x => x.Id).ToArrayAsync();
            var reservations = await db.WithdrawalReservations.Where(x => cassetteIds.Contains(x.CassetteId))
                .ToListAsync();
            Assert.Equal(3, reservations.Count);
            Assert.Equal(140, reservations.Sum(x => x.Count));
            Assert.All(reservations, x => Assert.InRange(x.Count, 1, 100));
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

    private static async Task<Guid> CreateAtm(HttpClient client, string name, int firstCount, int secondCount)
    {
        var atmId = (await Read(await client.PostAsJsonAsync("/api/atms", new { name }))).GetProperty("id").GetGuid();
        await Read(await client.PutAsJsonAsync($"/api/atms/{atmId}/configuration", new
        {
            cassettes = new[]
            {
                Cassette(1, "Recycling", "TRY", 100),
                Cassette(2, "Recycling", "TRY", 100),
                Cassette(3, "Recycling", "TRY", 20),
                Cassette(4, "Recycling", "USD", 100),
                Cassette(5, "DepositOnly", null, null)
            }
        }));
        await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/load", new
        {
            notes = new[]
            {
                Note(1, "TRY", 100, firstCount),
                Note(2, "TRY", 100, secondCount),
                Note(4, "USD", 100, 1)
            }
        }));
        await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { state = "IN_SERVICE" }));
        return atmId;
    }

    private static int Count(JsonElement[] notes, int cassetteNumber) => notes
        .Single(x => x.GetProperty("cassetteNumber").GetInt32() == cassetteNumber)
        .GetProperty("count").GetInt32();

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
