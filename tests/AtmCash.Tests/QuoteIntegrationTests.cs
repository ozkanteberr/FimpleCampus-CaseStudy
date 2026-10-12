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

public sealed class QuoteIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Quote_reserves_exact_or_neighbouring_notes_and_enforces_card_limit()
    {
        var adminConnection = Environment.GetEnvironmentVariable("ATM_TEST_PG")
            ?? "Host=127.0.0.1;Port=55432;Database=postgres;Username=atm_app;Password=atm_local_password";
        var databaseName = "atm_quote_test_" + Guid.NewGuid().ToString("N");
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

            var atmId = (await Read(await client.PostAsJsonAsync("/api/atms", new { name = "Quote ATM" })))
                .GetProperty("id").GetGuid();
            await Read(await client.PutAsJsonAsync($"/api/atms/{atmId}/configuration", new
            {
                cassettes = new[]
                {
                    Cassette(1, "Recycling", "TRY", 50),
                    Cassette(2, "Recycling", "TRY", 20),
                    Cassette(3, "Recycling", "USD", 100),
                    Cassette(4, "Recycling", "EUR", 100),
                    Cassette(5, "DepositOnly", null, null)
                }
            }));
            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/load", new
            {
                notes = new[]
                {
                    Note(1, "TRY", 50, 1), Note(2, "TRY", 20, 2),
                    Note(3, "USD", 100, 10), Note(4, "EUR", 100, 1)
                }
            }));

            const string cardNumber = "4111111111111111";
            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/quotes", new
            {
                cardNumber, currency = "TRY", amount = 80
            }), HttpStatusCode.Conflict, "INVALID_ATM_STATE");
            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { state = "IN_SERVICE" }));

            var quoteResponse = await client.PostAsJsonAsync($"/api/atms/{atmId}/quotes", new
            {
                cardNumber, currency = "TRY", amount = 80
            });
            Assert.Equal(HttpStatusCode.OK, quoteResponse.StatusCode);
            var quote = await Read(quoteResponse);
            var quoteId = quote.GetProperty("id").GetGuid();
            Assert.Equal(atmId, quote.GetProperty("atmId").GetGuid());
            Assert.Equal(80, quote.GetProperty("requestedAmount").GetInt32());
            Assert.DoesNotContain(cardNumber, quote.GetRawText());
            Assert.InRange((quote.GetProperty("expiresAt").GetDateTimeOffset() - DateTimeOffset.UtcNow).TotalSeconds, 0, 60);
            var alternatives = quote.GetProperty("alternatives").EnumerateArray().ToArray();
            Assert.Equal(2, alternatives.Length);
            var lower = alternatives.Single(x => x.GetProperty("kind").GetString() == "Lower");
            var upper = alternatives.Single(x => x.GetProperty("kind").GetString() == "Upper");
            Assert.Equal(70, lower.GetProperty("amount").GetInt32());
            Assert.Equal(90, upper.GetProperty("amount").GetInt32());
            Assert.Equal(1, Count(lower, 1));
            Assert.Equal(1, Count(lower, 2));
            Assert.Equal(1, Count(upper, 1));
            Assert.Equal(2, Count(upper, 2));

            var report = await Read(await client.GetAsync($"/api/atms/{atmId}/report"));
            Assert.Equal(1, report.GetProperty("cassettes")[0].GetProperty("reserved").GetInt32());
            Assert.Equal(2, report.GetProperty("cassettes")[1].GetProperty("reserved").GetInt32());
            Assert.Equal(0, report.GetProperty("cassettes")[0].GetProperty("available").GetInt32());
            Assert.Equal(0, report.GetProperty("cassettes")[1].GetProperty("available").GetInt32());
            Assert.True((await Read(await client.GetAsync($"/api/atms/{atmId}/consistency")))
                .GetProperty("consistent").GetBoolean());

            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/quotes", new
            {
                cardNumber = "4111111111111112", currency = "TRY", amount = 50
            }), HttpStatusCode.Conflict, "NO_PAYABLE_AMOUNT");

            var usdQuote = await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/quotes", new
            {
                cardNumber, currency = "USD", amount = 1000
            }));
            var exact = Assert.Single(usdQuote.GetProperty("alternatives").EnumerateArray().ToArray());
            Assert.Equal("Exact", exact.GetProperty("kind").GetString());
            Assert.Equal(1000, exact.GetProperty("amount").GetInt32());
            Assert.Equal(10, Count(exact, 3));
            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/quotes", new
            {
                cardNumber, currency = "USD", amount = 20
            }), HttpStatusCode.Conflict, "DAILY_LIMIT_EXCEEDED");

            var fingerprint = new CardIdentity(hmacKey).Fingerprint(cardNumber);
            var options = new DbContextOptionsBuilder<CashDbContext>().UseNpgsql(testConnection).Options;
            await using (var db = new CashDbContext(options))
            {
                var saved = await db.WithdrawalQuotes.Include(x => x.Reservations)
                    .SingleAsync(x => x.Id == quoteId);
                Assert.Equal(fingerprint, saved.CardFingerprint);
                Assert.Equal(90, saved.ReservedLimitAmount);
                Assert.Equal(2, saved.Reservations.Count);
                Assert.Equal(90, (await db.CardDailyUsages.SingleAsync(x => x.CardFingerprint == fingerprint
                    && x.Currency == Currency.TRY)).ReservedAmount);
                Assert.Equal(1000, (await db.CardDailyUsages.SingleAsync(x => x.CardFingerprint == fingerprint
                    && x.Currency == Currency.USD)).ReservedAmount);
                Assert.Equal(3, await db.Movements.CountAsync(x => x.Type == MovementType.RESERVATION));
                Assert.False(await db.CardDailyUsages.AnyAsync(x => x.CardFingerprint == cardNumber));
                Assert.Equal(2, await db.WithdrawalQuotes.CountAsync());
            }
            Assert.True((await Read(await client.GetAsync($"/api/atms/{atmId}/consistency")))
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

    private static int Count(JsonElement alternative, int cassetteNumber) => alternative.GetProperty("notes")
        .EnumerateArray().Single(x => x.GetProperty("cassetteNumber").GetInt32() == cassetteNumber)
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
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }
}
