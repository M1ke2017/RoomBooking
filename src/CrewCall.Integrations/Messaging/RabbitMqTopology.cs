using CrewCall.Contracts.Integration;
using RabbitMQ.Client;

namespace CrewCall.Integrations.Messaging;

/// <summary>
/// The broker topology (ADR-0014): one durable topic exchange for every integration event, and the integration-audit
/// queue bound to all of them. Declarations are idempotent, so publisher and consumer both declare what they use.
/// </summary>
public static class RabbitMqTopology
{
    public const string Exchange = IntegrationEventCatalog.Exchange;
    public const string AuditQueue = "crewcall.integration-audit";
    public const string AuditBinding = "#";

    public static Task DeclareExchangeAsync(IChannel channel, CancellationToken cancellationToken) =>
        channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);

    public static async Task DeclareAuditQueueAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await DeclareExchangeAsync(channel, cancellationToken);
        await channel.QueueDeclareAsync(AuditQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(AuditQueue, Exchange, AuditBinding, cancellationToken: cancellationToken);
    }
}
