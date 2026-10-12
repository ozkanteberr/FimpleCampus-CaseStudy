using AtmCash.Infrastructure;

public sealed class QuoteExpiryWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<QuoteExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dueIds = await scope.ServiceProvider.GetRequiredService<QuoteExpiryOperations>()
                    .FindDueIds(stoppingToken);
                foreach (var quoteId in dueIds)
                {
                    try
                    {
                        await using var quoteScope = scopes.CreateAsyncScope();
                        await quoteScope.ServiceProvider.GetRequiredService<QuoteExpiryOperations>()
                            .Expire(quoteId, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "Failed to expire quote {QuoteId}", quoteId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to scan expired quotes");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
