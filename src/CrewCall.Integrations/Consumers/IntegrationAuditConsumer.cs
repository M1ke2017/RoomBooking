using CrewCall.Contracts.Integration;
using CrewCall.Integrations.Messaging;
using CrewCall.Messaging;
using CrewCall.Persistence.Messaging;
using RabbitMQ.Client;

namespace CrewCall.Integrations.Consumers;

/// <summary>
/// Consumes every integration event from the crewcall.integration-audit queue (bound with "#"). Its effect, a technical
/// receipt, commits together with its inbox record (consumer "crewcall-integration-audit"), and only then is the message
/// ACKed (ADR-0014). Delivery semantics are in <see cref="IntegrationEventConsumer"/>.
/// </summary>
public sealed class IntegrationAuditConsumer(
    RabbitMqConnection connection, IntegrationAuditHandler handler, ILogger<IntegrationAuditConsumer> logger)
    : IntegrationEventConsumer(connection, logger)
{
    private readonly ILogger<IntegrationAuditConsumer> _logger = logger;

    protected override string DisplayName => "Integration audit consumer";

    protected override string QueueName => RabbitMqTopology.AuditQueue;

    protected override Task DeclareQueueAsync(IChannel channel, CancellationToken cancellationToken) =>
        RabbitMqTopology.DeclareAuditQueueAsync(channel, cancellationToken);

    protected override async Task HandleAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(envelope, cancellationToken);
        if (result == InboxResult.Duplicate)
        {
            _logger.LogInformation("Duplicate {Type} {MessageId} skipped.", envelope.Type, envelope.MessageId);
        }
        else
        {
            _logger.LogInformation("Received {Type} v{Version} {MessageId}.", envelope.Type, envelope.Version, envelope.MessageId);
        }
    }
}
