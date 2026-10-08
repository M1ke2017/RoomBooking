using Microsoft.Extensions.Options;

namespace CrewCall.Integrations;

/// <summary>
/// Polls the outbox (ADR-0014): drains full batches back to back, then waits for the next tick. Errors are logged and the
/// loop continues; stopping the host cancels it cleanly.
/// </summary>
public sealed class OutboxPublisherWorker(OutboxProcessor processor, IOptions<OutboxPublisherOptions> options, ILogger<OutboxPublisherWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.PollInterval);
        do
        {
            try
            {
                OutboxBatchResult result;
                do
                {
                    result = await processor.ProcessBatchAsync(stoppingToken);
                }
                while (result.Failed == 0 && result.Claimed == options.Value.BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Outbox publishing pass failed; retrying on the next tick.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
