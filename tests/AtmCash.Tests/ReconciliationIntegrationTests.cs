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

public sealed class ReconciliationIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Physical_counts_write_adjustments_and_preserve_rate_snapshot()
    {
        var adminConnection = Environment.GetEnvironmentVariable("ATM_TEST_PG")
            ?? "Host=127.0.0.1;Port=55432;Database=postgres;Username=atm_app;Password=atm_local_password";
        var databaseName = "atm_reconciliation_test_" + Guid.NewGuid().ToString("N");
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
            client.DefaultRequestHeaders.Add("X-User", "count-officer");
            var atmId = (await Read(await client.PostAsJsonAsync("/api/atms", new { name = "Count ATM" })))
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
                notes = new[]
                {
                    Note(1, "TRY", 100, 10), Note(2, "TRY", 20, 5),
                    Note(3, "USD", 100, 2), Note(5, "TRY", 50, 2), Note(5, "USD", 20, 1)
                }
            }));
            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { state = "IN_SERVICE" }));
            var counts = new[]
            {
                Physical(1, PhysicalNote("TRY", 100, 9)),
                Physical(2, PhysicalNote("TRY", 20, 6)),
                Physical(3, PhysicalNote("USD", 100, 3)),
                Physical(4),
                Physical(5, PhysicalNote("TRY", 50, 1), PhysicalNote("USD", 20, 2),
                    PhysicalNote("EUR", 50, 1))
            };
            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/reconciliations",
                new { cassettes = counts }), HttpStatusCode.Conflict, "INVALID_ATM_STATE");
            await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { state = "MAINTENANCE" }));
            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/reconciliations",
                new { cassettes = counts.Take(4).ToArray() }), HttpStatusCode.UnprocessableEntity,
                "INVALID_PHYSICAL_COUNT");

            var result = await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/reconciliations",
                new { cassettes = counts }));
            var reconciliationId = result.GetProperty("id").GetGuid();
            Assert.Equal("PendingApproval", result.GetProperty("status").GetString());
            Assert.Equal(7_220m, result.GetProperty("totalAbsoluteTryDifference").GetDecimal());
            var lines = result.GetProperty("lines").EnumerateArray().ToArray();
            Assert.Equal(6, lines.Length);
            Assert.Equal(-1, Line(lines, 1, "TRY", 100).GetProperty("deltaCount").GetInt32());
            Assert.Equal(1, Line(lines, 2, "TRY", 20).GetProperty("deltaCount").GetInt32());
            Assert.Equal(4_000m, Line(lines, 3, "USD", 100)
                .GetProperty("absoluteTryDifference").GetDecimal());
            Assert.Equal(40m, Line(lines, 5, "USD", 20).GetProperty("tryRateSnapshot").GetDecimal());
            Assert.Equal(2_250m, Line(lines, 5, "EUR", 50)
                .GetProperty("absoluteTryDifference").GetDecimal());

            var report = await Read(await client.GetAsync($"/api/atms/{atmId}/report"));
            Assert.Equal("MAINTENANCE", report.GetProperty("state").GetString());
            Assert.Equal(9, report.GetProperty("cassettes")[0].GetProperty("count").GetInt32());
            Assert.Equal(6, report.GetProperty("cassettes")[1].GetProperty("count").GetInt32());
            Assert.Equal(3, report.GetProperty("cassettes")[2].GetProperty("count").GetInt32());
            Assert.Equal(4, report.GetProperty("cassettes")[4].GetProperty("count").GetInt32());
            Assert.Equal(3, report.GetProperty("cassettes")[4].GetProperty("notes").GetArrayLength());
            Assert.True((await Read(await client.GetAsync($"/api/atms/{atmId}/consistency")))
                .GetProperty("consistent").GetBoolean());

            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/reconciliations",
                new { cassettes = counts }), HttpStatusCode.Conflict, "PENDING_RECONCILIATION");
            await ExpectError(client.PostAsJsonAsync($"/api/atms/{atmId}/state",
                new { state = "IN_SERVICE" }), HttpStatusCode.Conflict, "PENDING_RECONCILIATION");

            var options = new DbContextOptionsBuilder<CashDbContext>().UseNpgsql(testConnection).Options;
            await using var db = new CashDbContext(options);
            var saved = await db.Reconciliations.Include(x => x.Lines)
                .SingleAsync(x => x.Id == reconciliationId);
            Assert.Equal("count-officer", saved.CreatedBy);
            Assert.Equal(ReconciliationStatus.PendingApproval, saved.Status);
            Assert.Equal(5, JsonDocument.Parse(saved.PhysicalCountsJson).RootElement.GetArrayLength());
            Assert.Equal(6, saved.Lines.Count);
            Assert.Equal(6, await db.Movements.CountAsync(x => x.OperationId == reconciliationId
                && x.Type == MovementType.RECONCILIATION_ADJUSTMENT));
            db.ExchangeRates.Single(x => x.Currency == Currency.USD).TryRate = 41m;
            await db.SaveChangesAsync();
            Assert.Equal(40m, (await db.ReconciliationLines.SingleAsync(x => x.ReconciliationId == reconciliationId
                && x.Currency == Currency.USD && x.Denomination == 100)).TryRateSnapshot);
            Assert.Equal(7_220m, saved.TotalAbsoluteTryDifference);

            await ExpectError(client.PostAsync($"/api/reconciliations/{Guid.NewGuid()}/approve", null),
                HttpStatusCode.NotFound, "RECONCILIATION_NOT_FOUND");
            await ExpectError(client.PostAsync($"/api/reconciliations/{reconciliationId}/approve", null),
                HttpStatusCode.Conflict, "SELF_APPROVAL_FORBIDDEN");
            client.DefaultRequestHeaders.Remove("X-User");
            client.DefaultRequestHeaders.Add("X-User", "COUNT-OFFICER");
            await ExpectError(client.PostAsync($"/api/reconciliations/{reconciliationId}/approve", null),
                HttpStatusCode.Conflict, "SELF_APPROVAL_FORBIDDEN");
            client.DefaultRequestHeaders.Remove("X-User");
            client.DefaultRequestHeaders.Add("X-User", "review-officer");
            var approval = await Read(await client.PostAsync($"/api/reconciliations/{reconciliationId}/approve", null));
            Assert.Equal(reconciliationId, approval.GetProperty("id").GetGuid());
            Assert.Equal(atmId, approval.GetProperty("atmId").GetGuid());
            Assert.Equal("Approved", approval.GetProperty("status").GetString());
            Assert.Equal("review-officer", approval.GetProperty("approvedBy").GetString());
            Assert.NotEqual(default, approval.GetProperty("approvedAt").GetDateTimeOffset());
            await ExpectError(client.PostAsync($"/api/reconciliations/{reconciliationId}/approve", null),
                HttpStatusCode.Conflict, "RECONCILIATION_NOT_PENDING");
            await db.Entry(saved).ReloadAsync();
            Assert.Equal(ReconciliationStatus.Approved, saved.Status);
            Assert.Equal("review-officer", saved.ApprovedBy);
            Assert.NotNull(saved.ApprovedAt);
            Assert.Equal(6, await db.Movements.CountAsync(x => x.OperationId == reconciliationId
                && x.Type == MovementType.RECONCILIATION_ADJUSTMENT));

            var inService = await Read(await client.PostAsJsonAsync($"/api/atms/{atmId}/state",
                new { state = "IN_SERVICE" }));
            Assert.Equal("IN_SERVICE", inService.GetProperty("state").GetString());
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

    private static JsonElement Line(JsonElement[] lines, int cassetteNumber, string currency, int denomination) =>
        lines.Single(x => x.GetProperty("cassetteNumber").GetInt32() == cassetteNumber
            && x.GetProperty("currency").GetString() == currency
            && x.GetProperty("denomination").GetInt32() == denomination);

    private static object Physical(int cassetteNumber, params object[] notes) => new { cassetteNumber, notes };
    private static object PhysicalNote(string currency, int denomination, int count) =>
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
