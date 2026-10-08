using CrewCall.Contracts.Integration;
using RabbitMQ.Client;

namespace CrewCall.Messaging;

/// <summary>
/// The crewcall.events topic exchange (ADR-0014) and the consumer queues bound to it. Declarations are idempotent, so
/// every publisher and consumer declares what it uses.
/// </summary>
public static class IntegrationExchange
{
    public const string Name = IntegrationEventCatalog.Exchange;

    public static Task DeclareAsync(IChannel channel, CancellationToken cancellationToken) =>
        channel.ExchangeDeclareAsync(Name, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);

    /// <summary>A durable queue bound to the exchange with each of <paramref name="bindingKeys"/>.</summary>
    public static async Task DeclareQueueAsync(
        IChannel channel, string queue, IReadOnlyCollection<string> bindingKeys, CancellationToken cancellationToken)
    {
        await DeclareAsync(channel, cancellationToken);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        foreach (var bindingKey in bindingKeys)
        {
            await channel.QueueBindAsync(queue, Name, bindingKey, cancellationToken: cancellationToken);
        }
    }
}
