using CrewCall.Integrations.Messaging;
using CrewCall.Persistence.Messaging;
using Microsoft.Extensions.Options;

namespace CrewCall.Integrations;

/// <param name="Claimed">Messages leased in this batch.</param>
/// <param name="Published">Confirmed by the broker and marked processed.</param>
/// <param name="Failed">Attempts that failed (left pending for retry, or dead-lettered).</param>
public sealed record OutboxBatchResult(int Claimed, int Published, int Failed);

/// <summary>
/// One publishing pass over the outbox (ADR-0014): claim a batch (a short statement with FOR UPDATE SKIP LOCKED and a
/// lease), publish each message with a broker confirmation, and only then mark it processed. A failure records the
/// attempt, the error and the next eligible time (backoff); after <see cref="OutboxPublisherOptions.MaxAttempts"/> the
/// message is dead-lettered. The rest of the batch is released and retried on the next pass, so a broker outage costs
/// one attempt per pass, not one per message. No database transaction is held while publishing.
/// </summary>
public sealed class OutboxProcessor(
    IServiceScopeFactory scopes,
    IIntegrationMessagePublisher publisher,
    IOptions<OutboxPublisherOptions> options,
    TimeProvider clock,
    ILogger<OutboxProcessor> logger)
{
    /// <summary>Identifies this publisher instance in leases (several may run at once).</summary>
    public string PublisherId { get; } = NewPublisherId();

    private static string NewPublisherId()
    {
        var id = $"{Environment.MachineName}/{Environment.ProcessId}/{Guid.NewGuid():N}";
        return id.Length <= OutboxMessage.LockedByMaxLength ? id : id[^OutboxMessage.LockedByMaxLength..];
    }

    public async Task<OutboxBatchResult> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<OutboxStore>();

        var claimed = await store.ClaimAsync(PublisherId, settings.BatchSize, settings.LeaseDuration, clock.GetUtcNow(), cancellationToken);
        if (claimed.Count == 0)
        {
            return new OutboxBatchResult(0, 0, 0);
        }

        logger.LogInformation("Outbox batch claimed: {Count} message(s).", claimed.Count);

        var published = 0;
        for (var index = 0; index < claimed.Count; index++)
        {
            var message = claimed[index];
            try
            {
                await publisher.PublishAsync(message, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // shutting down: the leases expire and the messages are claimed again
            }
            catch (Exception exception)
            {
                var attempt = message.AttemptCount + 1;
                var deadLettered = attempt >= settings.MaxAttempts;
                var now = clock.GetUtcNow();
                await store.MarkAttemptFailedAsync(
                    message.Id, $"{exception.GetType().Name}: {exception.Message}", now, now + settings.RetryDelay(attempt), deadLettered, cancellationToken);

                if (deadLettered)
                {
                    logger.LogError(exception, "Outbox message {MessageId} ({Type}) failed {Attempts} times and was dead-lettered.", message.Id, message.Type, attempt);
                }
                else
                {
                    logger.LogWarning(exception, "Publishing outbox message {MessageId} ({Type}) failed (attempt {Attempt}); it stays pending.", message.Id, message.Type, attempt);
                }

                // Most failures are the broker's: stop here and give the rest back instead of failing them one by one.
                await store.ReleaseAsync(PublisherId, claimed.Skip(index + 1).Select(rest => rest.Id).ToList(), cancellationToken);
                return new OutboxBatchResult(claimed.Count, published, 1);
            }

            // Confirmed by the broker: only now is the message processed. A crash before this line republishes it.
            await store.MarkPublishedAsync(message.Id, clock.GetUtcNow(), cancellationToken);
            published++;
            logger.LogInformation("Published {Type} v{Version} {MessageId}.", message.Type, message.Version, message.Id);
        }

        return new OutboxBatchResult(claimed.Count, published, 0);
    }
}
