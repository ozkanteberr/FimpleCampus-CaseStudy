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

public sealed class MaintenanceQuoteIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Entering_maintenance_cancels_all_active_quotes_and_releases_their_reservations()
    {
        var adminConnection = Environment.GetEnvironmentVariable("ATM_TEST_PG")
            ?? "Host=127.0.0.1;Port=55432;Database=postgres;Username=atm_app;Password=atm_local_password";
        var databaseName = "atm_maintenance_quote_test_" + Guid.NewGuid().ToString("N");
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
            client.DefaultRequestHeaders.Add("X-User", "setup-officer");

            var atmId = (await Read(await client.PostAsJsonAsync("/api/atms", new { name = "Maintenance Quote ATM" })))
                .GetProperty("id").GetGuid();
            await Read(await client.PutAsJsonAsync($"/api/atms/{atmId}/configuration", new
            {
                cassettes = new object[]
                {
                    Cassette(1, "Recycling", "TRY", 50), Cassette(2, "Recycling", "TRY", 20),
                    Cassette(3, "Recycling", "USD", 100), Cassette(4, "Recycling", "EUR", 100),
                    Cassette(5, "DepositOnly", null, null)
                }
            }));
            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/load", new
            {
                notes = new[] { Note(1, "TRY", 50, 2), Note(2, "TRY", 20, 3) }
            }));
            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { state = "IN_SERVICE" }));
            var cards = new[] { "4111111111111111", "4111111111111112" };
            var quotes = new List<(Guid Id, Guid AlternativeId)>();
            foreach (var cardNumber in cards)
            {
                var quote = await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/quotes", new
                {
                    cardNumber, currency = "TRY", amount = 80
                }));
                quotes.Add((quote.GetProperty("id").GetGuid(),
                    quote.GetProperty("alternatives")[0].GetProperty("id").GetGuid()));
            }
            var reservedReport = await Read(await client.GetAsync($"/api/atms/{atmId}/report"));
            Assert.Equal(2, reservedReport.GetProperty("cassettes")[0].GetProperty("reserved").GetInt32());
            Assert.Equal(3, reservedReport.GetProperty("cassettes")[1].GetProperty("reserved").GetInt32());

            var maintenance = await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/state",
                new { state = "MAINTENANCE" }));
            Assert.Equal("MAINTENANCE", maintenance.GetProperty("state").GetString());
            foreach (var (quoteId, alternativeId) in quotes)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/quotes/{quoteId}/confirm")
                {
                    Content = JsonContent.Create(new { alternativeId })
                };
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
                await ExpectError(client.SendAsync(request), HttpStatusCode.Conflict, "QUOTE_NOT_ACTIVE");
            }
            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/quotes", new
            {
                cardNumber = cards[0], currency = "TRY", amount = 80
            }), HttpStatusCode.Conflict, "INVALID_ATM_STATE");

            var report = await Read(await client.GetAsync($"/api/atms/{atmId}/report"));
            Assert.Equal(2, report.GetProperty("cassettes")[0].GetProperty("count").GetInt32());
            Assert.Equal(3, report.GetProperty("cassettes")[1].GetProperty("count").GetInt32());
            Assert.Equal(0, report.GetProperty("cassettes")[0].GetProperty("reserved").GetInt32());
            Assert.Equal(0, report.GetProperty("cassettes")[1].GetProperty("reserved").GetInt32());
            Assert.True((await Read(await client.GetAsync($"/api/atms/{atmId}/consistency")))
                .GetProperty("consistent").GetBoolean());

            var options = new DbContextOptionsBuilder<CashDbContext>().UseNpgsql(testConnection).Options;
            var quoteIds = quotes.Select(x => x.Id).ToArray();
            await using (var db = new CashDbContext(options))
            {
                Assert.Equal(2, await db.WithdrawalQuotes.CountAsync(x => x.AtmId == atmId
                    && x.Status == WithdrawalQuoteStatus.Cancelled));
                foreach (var cardNumber in cards)
                {
                    var fingerprint = new CardIdentity(hmacKey).Fingerprint(cardNumber);
                    var usage = await db.CardDailyUsages.SingleAsync(x => x.CardFingerprint == fingerprint
                        && x.Currency == Currency.TRY);
                    Assert.Equal(0, usage.ReservedAmount);
                    Assert.Equal(0, usage.UsedAmount);
                }
                Assert.Equal(4, await db.Movements.CountAsync(x => quoteIds.Contains(x.OperationId)
                    && x.Type == MovementType.RESERVATION_RELEASE));
                Assert.Equal(0, await db.Movements.CountAsync(x => quoteIds.Contains(x.OperationId)
                    && x.Type == MovementType.WITHDRAWAL));
            }

            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { state = "IN_SERVICE" }));
            var nextQuote = await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/quotes", new
            {
                cardNumber = cards[0], currency = "TRY", amount = 80
            }));
            Assert.Equal(2, nextQuote.GetProperty("alternatives").GetArrayLength());
            var maintenanceTask = client.PostAsJsonAsync($"/api/atms/{atmId}/state",
                new { state = "MAINTENANCE" });
            var concurrentQuoteTask = client.PostAsJsonAsync($"/api/atms/{atmId}/quotes", new
            {
                cardNumber = "4111111111111113", currency = "TRY", amount = 70
            });
            await Task.WhenAll(maintenanceTask, concurrentQuoteTask);
            await Read(await maintenanceTask);
            using (var concurrentQuote = await concurrentQuoteTask)
            {
                Assert.Contains(concurrentQuote.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
                if (concurrentQuote.StatusCode == HttpStatusCode.Conflict)
                {
                    using var body = JsonDocument.Parse(await concurrentQuote.Content.ReadAsStringAsync());
                    Assert.Equal("INVALID_ATM_STATE", body.RootElement.GetProperty("code").GetString());
                }
            }
            await using (var db = new CashDbContext(options))
                Assert.False(await db.WithdrawalQuotes.AnyAsync(x => x.AtmId == atmId
                    && x.Status == WithdrawalQuoteStatus.Active));
            var finalReport = await Read(await client.GetAsync($"/api/atms/{atmId}/report"));
            Assert.All(finalReport.GetProperty("cassettes").EnumerateArray(),
                cassette => Assert.Equal(0, cassette.GetProperty("reserved").GetInt32()));
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
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }
}
