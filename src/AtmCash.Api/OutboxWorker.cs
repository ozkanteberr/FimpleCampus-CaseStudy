using AtmCash.Infrastructure;

public sealed class OutboxWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    for (var count = 0; count < 100; count++)
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        if (!await scope.ServiceProvider.GetRequiredService<OutboxOperations>()
                            .ProcessNext(stoppingToken)) break;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Failed to process pending outbox event");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
