using CrewCall.Contracts.Integration;
using RabbitMQ.Client;

namespace CrewCall.Integrations.Messaging;

/// <summary>
/// The broker topology (ADR-0014, ADR-0015): one durable topic exchange for every integration event; the integration-audit
/// queue bound to all of them; the live-operations queue bound to the routing keys it turns into live messages. Declarations are idempotent, so publisher and consumer both declare what they use.
/// </summary>
public static class RabbitMqTopology
{
    public const string Exchange = IntegrationEventCatalog.Exchange;
    public const string AuditQueue = "crewcall.integration-audit";
    public const string AuditBinding = "#";

    public static Task DeclareExchangeAsync(IChannel channel, CancellationToken cancellationToken) =>
        channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);

    public static Task DeclareAuditQueueAsync(IChannel channel, CancellationToken cancellationToken) =>
        DeclareQueueAsync(channel, AuditQueue, [AuditBinding], cancellationToken);

    /// <summary>A durable queue bound to the exchange with each of <paramref name="bindingKeys"/>.</summary>
    public static async Task DeclareQueueAsync(
        IChannel channel, string queue, IReadOnlyCollection<string> bindingKeys, CancellationToken cancellationToken)
    {
        await DeclareExchangeAsync(channel, cancellationToken);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        foreach (var bindingKey in bindingKeys)
        {
            await channel.QueueBindAsync(queue, Exchange, bindingKey, cancellationToken: cancellationToken);
        }
    }
}
