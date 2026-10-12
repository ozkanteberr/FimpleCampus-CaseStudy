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

public sealed class ConcurrentCardLimitIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Parallel_quotes_on_different_atms_share_the_same_card_daily_limit()
    {
        var adminConnection = Environment.GetEnvironmentVariable("ATM_TEST_PG")
            ?? "Host=127.0.0.1;Port=55432;Database=postgres;Username=atm_app;Password=atm_local_password";
        var databaseName = "atm_parallel_card_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
                await create.ExecuteNonQueryAsync();
            var testConnection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
            const string hmacKey = "integration-test-card-hmac-key-with-32-bytes";
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:CashDb"] = testConnection,
                    ["CardIdentity:HmacKey"] = hmacKey
                })));
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User", "cash-officer");
            var atmIds = new[]
            {
                await CreateAtm(client, "First limit ATM"),
                await CreateAtm(client, "Second limit ATM")
            };

            const string firstCard = "4111111111111111";
            var complementary = await SendTogether(client,
                (atmIds[0], firstCard, 600), (atmIds[1], firstCard, 400));
            var firstQuotes = new List<JsonElement>();
            foreach (var response in complementary)
            {
                using (response)
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    firstQuotes.Add(body.RootElement.Clone());
                }
            }
            Assert.Equal(1_000, firstQuotes.Sum(x => x.GetProperty("alternatives")[0]
                .GetProperty("amount").GetInt32()));
            Assert.Equal(2, firstQuotes.Select(x => x.GetProperty("atmId").GetGuid()).Distinct().Count());

            const string secondCard = "4111111111111112";
            var competing = await SendTogether(client,
                (atmIds[0], secondCard, 600), (atmIds[1], secondCard, 600));
            var successCount = 0;
            var conflictCount = 0;
            foreach (var response in competing)
            {
                using (response)
                {
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        successCount++;
                        Assert.Equal(600, body.RootElement.GetProperty("alternatives")[0]
                            .GetProperty("amount").GetInt32());
                    }
                    else
                    {
                        conflictCount++;
                        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                        Assert.Equal("DAILY_LIMIT_EXCEEDED", body.RootElement.GetProperty("code").GetString());
                    }
                }
            }
            Assert.Equal(1, successCount);
            Assert.Equal(1, conflictCount);

            var options = new DbContextOptionsBuilder<CashDbContext>().UseNpgsql(testConnection).Options;
            await using var db = new CashDbContext(options);
            var firstFingerprint = new CardIdentity(hmacKey).Fingerprint(firstCard);
            var secondFingerprint = new CardIdentity(hmacKey).Fingerprint(secondCard);
            var firstUsage = await db.CardDailyUsages.SingleAsync(x => x.CardFingerprint == firstFingerprint
                && x.Currency == Currency.USD);
            var secondUsage = await db.CardDailyUsages.SingleAsync(x => x.CardFingerprint == secondFingerprint
                && x.Currency == Currency.USD);
            Assert.Equal(1_000, firstUsage.ReservedAmount);
            Assert.Equal(600, secondUsage.ReservedAmount);
            Assert.Equal(0, firstUsage.UsedAmount);
            Assert.Equal(0, secondUsage.UsedAmount);
            Assert.Equal(2, await db.WithdrawalQuotes.CountAsync(x => x.CardFingerprint == firstFingerprint
                && x.Status == WithdrawalQuoteStatus.Active));
            Assert.Equal(1, await db.WithdrawalQuotes.CountAsync(x => x.CardFingerprint == secondFingerprint
                && x.Status == WithdrawalQuoteStatus.Active));
            Assert.Equal(3, await db.Movements.CountAsync(x => x.Type == MovementType.RESERVATION));
            foreach (var atmId in atmIds)
            {
                var report = await Read(await client.GetAsync($"/api/atms/{atmId}/report"));
                var usdCassette = report.GetProperty("cassettes")[2];
                Assert.Equal(20, usdCassette.GetProperty("count").GetInt32());
                Assert.InRange(usdCassette.GetProperty("reserved").GetInt32(), 0, 20);
                Assert.True((await Read(await client.GetAsync($"/api/atms/{atmId}/consistency")))
                    .GetProperty("consistent").GetBoolean());
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

    private static async Task<HttpResponseMessage[]> SendTogether(HttpClient client,
        (Guid AtmId, string Card, int Amount) first, (Guid AtmId, string Card, int Amount) second)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> Send((Guid AtmId, string Card, int Amount) request)
        {
            await start.Task;
            return await client.PostAsJsonAsync($"/api/atms/{request.AtmId}/quotes", new
            {
                cardNumber = request.Card, currency = "USD", amount = request.Amount
            });
        }
        var tasks = new[] { Send(first), Send(second) };
        start.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static async Task<Guid> CreateAtm(HttpClient client, string name)
    {
        var atmId = (await Read(await client.PostAsJsonAsync("/api/atms", new { name })))
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
            notes = new[] { Note(3, "USD", 100, 20) }
        }));
        await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { state = "IN_SERVICE" }));
        return atmId;
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
