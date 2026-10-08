using CrewCall.Messaging;
using RabbitMQ.Client;

namespace CrewCall.Integrations.Messaging;

/// <summary>
/// This service's part of the broker topology (ADR-0014, ADR-0015): the crewcall.events exchange
/// (<see cref="IntegrationExchange"/>) and the integration-audit queue bound to every event. The live-operations queue
/// is declared by its consumer.
/// </summary>
public static class RabbitMqTopology
{
    public const string Exchange = IntegrationExchange.Name;
    public const string AuditQueue = "crewcall.integration-audit";
    public const string AuditBinding = "#";

    public static Task DeclareExchangeAsync(IChannel channel, CancellationToken cancellationToken) =>
        IntegrationExchange.DeclareAsync(channel, cancellationToken);

    public static Task DeclareAuditQueueAsync(IChannel channel, CancellationToken cancellationToken) =>
        IntegrationExchange.DeclareQueueAsync(channel, AuditQueue, [AuditBinding], cancellationToken);
}
