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
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }
}
