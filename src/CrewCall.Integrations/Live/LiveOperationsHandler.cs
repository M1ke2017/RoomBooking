using CrewCall.Contracts.Integration;
using CrewCall.Persistence.Messaging;
using Microsoft.Extensions.Options;

namespace CrewCall.Integrations.Live;

public enum LiveDeliveryResult
{
    /// <summary>Mapped, broadcast and recorded in the consumer's inbox.</summary>
    Published,

    /// <summary>This consumer handled the message before: nothing was broadcast again.</summary>
    Duplicate,

    /// <summary>A type or version without a live mapping: ignored on purpose, nothing broadcast or recorded.</summary>
    Unsupported
}

/// <summary>
/// The live-operations consumer's processing of one message (ADR-0015):
/// inbox check → map → SignalR broadcast → inbox record. The caller ACKs only after this returned.
/// The broadcast and the inbox record cannot share a transaction: if the process dies between them, the redelivered
/// message is broadcast again (with the same MessageId, which the browser uses to deduplicate). At-least-once, by design.
/// </summary>
public sealed class LiveOperationsHandler(
    IServiceScopeFactory scopes, ILiveOperationsPublisher publisher, IOptions<LiveOperationsOptions> options, TimeProvider clock)
{
    public async Task<LiveDeliveryResult> HandleAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken)
    {
        var consumerName = options.Value.ConsumerName;
        await using var scope = scopes.CreateAsyncScope();
        var inbox = scope.ServiceProvider.GetRequiredService<InboxStore>();

        if (await inbox.IsProcessedAsync(consumerName, envelope.MessageId, cancellationToken))
        {
            return LiveDeliveryResult.Duplicate;
        }

        if (LiveOperationMapper.ReadEvent(envelope) is not { } integrationEvent)
        {
            return LiveDeliveryResult.Unsupported;
        }

        var routing = await scope.ServiceProvider.GetRequiredService<LiveRoutingLookup>().ResolveAsync(integrationEvent, cancellationToken);
        var message = LiveOperationMapper.Map(envelope, integrationEvent);

        // A failure here throws before anything is recorded, so the message is redelivered and broadcast again.
        await publisher.PublishAsync(message, LiveOperationGroups.For(integrationEvent, routing), cancellationToken);
        await inbox.MarkProcessedAsync(consumerName, envelope, clock.GetUtcNow(), cancellationToken);
        return LiveDeliveryResult.Published;
    }
}
