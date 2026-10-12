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

public sealed class ConcurrentAtmQuoteIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Parallel_quotes_cannot_reserve_the_same_atm_notes_twice()
    {
        var adminConnection = Environment.GetEnvironmentVariable("ATM_TEST_PG")
            ?? "Host=127.0.0.1;Port=55432;Database=postgres;Username=atm_app;Password=atm_local_password";
        var databaseName = "atm_parallel_quote_test_" + Guid.NewGuid().ToString("N");
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
            var atmId = (await Read(await client.PostAsJsonAsync("/api/atms", new { name = "Concurrent ATM" })))
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
            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/load", new
            {
                notes = new[] { Note(1, "TRY", 100, 1), Note(2, "TRY", 100, 1) }
            }));
            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { state = "IN_SERVICE" }));

            const int requestCount = 12;
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = Enumerable.Range(0, requestCount).Select(async index =>
            {
                await start.Task;
                return await client.PostAsJsonAsync($"/api/atms/{atmId}/quotes", new
                {
                    cardNumber = $"411111111111{index:D4}", currency = "TRY", amount = 100
                });
            }).ToArray();
            start.SetResult();
            var responses = await Task.WhenAll(requests);
            var successfulQuotes = new List<JsonElement>();
            foreach (var response in responses)
            {
                using (response)
                {
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (response.StatusCode == HttpStatusCode.OK)
                        successfulQuotes.Add(body.RootElement.Clone());
                    else
                    {
                        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                        Assert.Equal("NO_PAYABLE_AMOUNT", body.RootElement.GetProperty("code").GetString());
                    }
                }
            }
            Assert.Equal(2, successfulQuotes.Count);
            Assert.Equal(2, successfulQuotes.Select(x => x.GetProperty("id").GetGuid()).Distinct().Count());
            var selectedCassettes = successfulQuotes.Select(x =>
            {
                var alternative = Assert.Single(x.GetProperty("alternatives").EnumerateArray().ToArray());
                Assert.Equal("Exact", alternative.GetProperty("kind").GetString());
                Assert.Equal(100, alternative.GetProperty("amount").GetInt32());
                var note = Assert.Single(alternative.GetProperty("notes").EnumerateArray().ToArray());
                Assert.Equal(1, note.GetProperty("count").GetInt32());
                return note.GetProperty("cassetteNumber").GetInt32();
            }).ToArray();
            Assert.Equal(new[] { 1, 2 }, selectedCassettes.OrderBy(x => x).ToArray());

            var report = await Read(await client.GetAsync($"/api/atms/{atmId}/report"));
            foreach (var cassette in report.GetProperty("cassettes").EnumerateArray().Take(2))
            {
                Assert.Equal(1, cassette.GetProperty("count").GetInt32());
                Assert.Equal(1, cassette.GetProperty("reserved").GetInt32());
                Assert.Equal(0, cassette.GetProperty("available").GetInt32());
            }
            Assert.True((await Read(await client.GetAsync($"/api/atms/{atmId}/consistency")))
                .GetProperty("consistent").GetBoolean());

            var options = new DbContextOptionsBuilder<CashDbContext>().UseNpgsql(testConnection).Options;
            await using var db = new CashDbContext(options);
            Assert.Equal(2, await db.WithdrawalQuotes.CountAsync(x => x.AtmId == atmId
                && x.Status == WithdrawalQuoteStatus.Active));
            Assert.Equal(2, await db.WithdrawalReservations.CountAsync());
            Assert.Equal(2, await db.WithdrawalReservations.SumAsync(x => x.Count));
            Assert.Equal(2, await db.Movements.CountAsync(x => x.Type == MovementType.RESERVATION));
            Assert.Equal(2, await db.CardDailyUsages.CountAsync(x => x.Currency == Currency.TRY));
            Assert.Equal(200, await db.CardDailyUsages.Where(x => x.Currency == Currency.TRY)
                .SumAsync(x => x.ReservedAmount));
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
}
