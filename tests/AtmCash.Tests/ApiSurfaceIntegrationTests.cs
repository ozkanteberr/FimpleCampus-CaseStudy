using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AtmCash.Tests;

public sealed class ApiSurfaceIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Fresh_api_exposes_health_openapi_swagger_and_problem_details()
    {
        var adminConnection = Environment.GetEnvironmentVariable("ATM_TEST_PG")
            ?? "Host=127.0.0.1;Port=55432;Database=postgres;Username=atm_app;Password=atm_local_password";
        var databaseName = "atm_surface_test_" + Guid.NewGuid().ToString("N");
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

            var health = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal("Healthy", await health.Content.ReadAsStringAsync());

            var openApi = await client.GetAsync("/openapi/v1.json");
            Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
            using (var document = JsonDocument.Parse(await openApi.Content.ReadAsStringAsync()))
                Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/atms", out _));

            var swagger = await client.GetAsync("/swagger");
            Assert.Equal(HttpStatusCode.OK, swagger.StatusCode);
            Assert.Contains("Swagger UI", await swagger.Content.ReadAsStringAsync());

            var rates = await client.GetFromJsonAsync<JsonElement>("/api/exchange-rates");
            Assert.Equal(3, rates.GetArrayLength());

            await AssertProblem(await client.GetAsync("/api/no-such-route"), HttpStatusCode.NotFound, "HTTP_404");
            await AssertProblem(await client.PostAsJsonAsync("/api/atms", new { name = "" }),
                HttpStatusCode.UnprocessableEntity, "INVALID_NAME");
            await AssertProblem(await client.PostAsync("/api/atms", new StringContent("{", System.Text.Encoding.UTF8, "application/json")),
                HttpStatusCode.BadRequest, "INVALID_REQUEST");
            await AssertProblem(await client.PostAsync("/api/atms", new StringContent("{}", System.Text.Encoding.UTF8, "text/plain")),
                HttpStatusCode.UnsupportedMediaType, "HTTP_415");
            await AssertProblem(await client.GetAsync($"/api/quotes/{Guid.NewGuid()}/cancel"),
                HttpStatusCode.MethodNotAllowed, "HTTP_405");

            var created = await client.PostAsJsonAsync("/api/atms", new { name = "Contract ATM" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var atm = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var atmId = atm.RootElement.GetProperty("id").GetGuid();

            await AssertProblem(await client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { }),
                HttpStatusCode.BadRequest, "INVALID_REQUEST");
            await AssertProblem(await client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { state = "UNKNOWN" }),
                HttpStatusCode.BadRequest, "INVALID_REQUEST");
            await AssertProblem(await client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { state = "IN_SERVICE" }),
                HttpStatusCode.Conflict, "INVALID_STATE_TRANSITION");
            await AssertProblem(await client.PutAsJsonAsync($"/api/atms/{atmId}/configuration",
                new { cassettes = new object?[] { null, null, null, null, null } }),
                HttpStatusCode.UnprocessableEntity, "INVALID_CONFIGURATION");
            await AssertProblem(await client.PostAsJsonAsync($"/api/atms/{atmId}/load",
                new { notes = new object?[] { null } }),
                HttpStatusCode.BadRequest, "INVALID_USER");
            client.DefaultRequestHeaders.Add("X-User", "contract-officer");
            await AssertProblem(await client.PostAsJsonAsync($"/api/atms/{atmId}/load",
                new { notes = new object?[] { null } }),
                HttpStatusCode.UnprocessableEntity, "INVALID_CASH_ENTRY");
            await AssertProblem(await client.PostAsJsonAsync($"/api/atms/{atmId}/deposits",
                new { notes = new object?[] { null }, unrecognisedCount = 0 }),
                HttpStatusCode.UnprocessableEntity, "INVALID_DEPOSIT_NOTE");
            await AssertProblem(await client.PostAsJsonAsync($"/api/atms/{atmId}/reconciliations",
                new { cassettes = new object?[] { null, null, null, null, null } }),
                HttpStatusCode.UnprocessableEntity, "INVALID_PHYSICAL_COUNT");
            await AssertProblem(await client.PostAsJsonAsync($"/api/atms/{atmId}/reconciliations",
                new { cassettes = Enumerable.Range(1, 5).Select(number => new
                {
                    cassetteNumber = number,
                    notes = number == 1 ? new object?[] { null } : Array.Empty<object>()
                }) }),
                HttpStatusCode.UnprocessableEntity, "INVALID_PHYSICAL_COUNT");
            await AssertProblem(await client.PostAsJsonAsync($"/api/quotes/{Guid.NewGuid()}/confirm",
                new { alternativeId = Guid.NewGuid() }),
                HttpStatusCode.BadRequest, "INVALID_IDEMPOTENCY_KEY");
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

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)status, document.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("title").GetString()));
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("traceId").GetString()));
    }
}
