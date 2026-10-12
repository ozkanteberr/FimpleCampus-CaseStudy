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

public sealed class QuoteConfirmationIntegrationTests
{
    [Theory]
    [InlineData("Lower", 70, 1)]
    [InlineData("Upper", 90, 0)]
    [Trait("Category", "Integration")]
    public async Task Confirm_debits_only_selected_notes_and_releases_unused_reservation(
        string kind, int amount, int remainingTwenties)
    {
        var adminConnection = Environment.GetEnvironmentVariable("ATM_TEST_PG")
            ?? "Host=127.0.0.1;Port=55432;Database=postgres;Username=atm_app;Password=atm_local_password";
        var databaseName = "atm_confirm_test_" + Guid.NewGuid().ToString("N");
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

            var atmId = (await Read(await client.PostAsJsonAsync("/api/atms", new { name = "Confirm ATM" })))
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
                notes = new[] { Note(1, "TRY", 50, 1), Note(2, "TRY", 20, 2) }
            }));
            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { state = "IN_SERVICE" }));
            var quote = await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/quotes", new
            {
                cardNumber = "4111111111111111", currency = "TRY", amount = 80
            }));
            var quoteId = quote.GetProperty("id").GetGuid();
            var alternatives = quote.GetProperty("alternatives").EnumerateArray().ToArray();
            var selectedId = alternatives.Single(x => x.GetProperty("kind").GetString() == kind)
                .GetProperty("id").GetGuid();
            var otherId = alternatives.Single(x => x.GetProperty("kind").GetString() != kind)
                .GetProperty("id").GetGuid();

            var key = "confirmed-" + Guid.NewGuid().ToString("N");
            await ExpectError(Confirm(client, quoteId, Guid.NewGuid(), key),
                HttpStatusCode.NotFound, "ALTERNATIVE_NOT_FOUND");
            await ExpectError(client.PostAsJsonAsync($"/api/quotes/{quoteId}/confirm",
                new { alternativeId = selectedId }), HttpStatusCode.BadRequest, "INVALID_IDEMPOTENCY_KEY");
            var responses = await Task.WhenAll(Confirm(client, quoteId, selectedId, key),
                Confirm(client, quoteId, selectedId, key));
            var confirmation = await Read(responses[0]);
            var replay = await Read(responses[1]);
            Assert.Equal(confirmation.GetRawText(), replay.GetRawText());
            Assert.Equal(quoteId, confirmation.GetProperty("quoteId").GetGuid());
            Assert.Equal(selectedId, confirmation.GetProperty("alternativeId").GetGuid());
            Assert.Equal(amount, confirmation.GetProperty("amount").GetInt32());
            Assert.Equal(1, NoteCount(confirmation, 1));
            Assert.Equal(2 - remainingTwenties, NoteCount(confirmation, 2));

            await ExpectError(Confirm(client, quoteId, otherId, key),
                HttpStatusCode.Conflict, "IDEMPOTENCY_CONFLICT");
            await ExpectError(Confirm(client, Guid.NewGuid(), selectedId, key),
                HttpStatusCode.Conflict, "IDEMPOTENCY_CONFLICT");
            await ExpectError(Confirm(client, quoteId, otherId, "new-" + Guid.NewGuid().ToString("N")),
                HttpStatusCode.Conflict, "QUOTE_NOT_ACTIVE");
            var report = await Read(await client.GetAsync($"/api/atms/{atmId}/report"));
            Assert.Equal(0, report.GetProperty("cassettes")[0].GetProperty("count").GetInt32());
            Assert.Equal(remainingTwenties, report.GetProperty("cassettes")[1].GetProperty("count").GetInt32());
            Assert.Equal(0, report.GetProperty("cassettes")[0].GetProperty("reserved").GetInt32());
            Assert.Equal(0, report.GetProperty("cassettes")[1].GetProperty("reserved").GetInt32());
            Assert.True((await Read(await client.GetAsync($"/api/atms/{atmId}/consistency")))
                .GetProperty("consistent").GetBoolean());

            var options = new DbContextOptionsBuilder<CashDbContext>().UseNpgsql(testConnection).Options;
            await using var db = new CashDbContext(options);
            var saved = await db.WithdrawalQuotes.SingleAsync(x => x.Id == quoteId);
            Assert.Equal(WithdrawalQuoteStatus.Confirmed, saved.Status);
            Assert.Equal(selectedId, saved.SelectedAlternativeId);
            Assert.NotNull(saved.ConfirmedAt);
            var fingerprint = new CardIdentity(hmacKey).Fingerprint("4111111111111111");
            var usage = await db.CardDailyUsages.SingleAsync(x => x.CardFingerprint == fingerprint
                && x.Currency == Currency.TRY);
            Assert.Equal(amount, usage.UsedAmount);
            Assert.Equal(0, usage.ReservedAmount);
            Assert.Equal(2, await db.Movements.CountAsync(x => x.OperationId == quoteId
                && x.Type == MovementType.RESERVATION_RELEASE));
            Assert.Equal(2, await db.Movements.CountAsync(x => x.OperationId == quoteId
                && x.Type == MovementType.WITHDRAWAL));
            Assert.Equal(1, await db.WithdrawalConfirmations.CountAsync());
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

    private static int NoteCount(JsonElement confirmation, int cassetteNumber) => confirmation.GetProperty("notes")
        .EnumerateArray().Single(x => x.GetProperty("cassetteNumber").GetInt32() == cassetteNumber)
        .GetProperty("count").GetInt32();

    private static object Note(int cassetteNumber, string currency, int denomination, int count) =>
        new { cassetteNumber, currency, denomination, count };

    private static object Cassette(int number, string kind, string? currency, int? denomination) =>
        new { number, kind, currency, denomination };

    private static async Task<HttpResponseMessage> Confirm(HttpClient client, Guid quoteId, Guid alternativeId, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/quotes/{quoteId}/confirm")
        {
            Content = JsonContent.Create(new { alternativeId })
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

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
