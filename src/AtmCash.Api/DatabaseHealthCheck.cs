using AtmCash.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;

public sealed class DatabaseHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
        return await db.Database.CanConnectAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Database unavailable.");
    }
}
