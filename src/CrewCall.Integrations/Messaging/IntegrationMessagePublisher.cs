using System.Text.Json;
using CrewCall.Contracts.Integration;
using CrewCall.Messaging;
using CrewCall.Persistence.Messaging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace CrewCall.Integrations.Messaging;

/// <summary>Publishes one outbox message; completes only once the broker confirmed it, throws otherwise.</summary>
public interface IIntegrationMessagePublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Publishes to the crewcall.events topic exchange with publisher confirms (ADR-0014): a channel with confirmation
/// tracking, so BasicPublishAsync returns only after the broker acknowledged the message, and throws when the broker
/// nacks it, the channel closes or the confirmation does not arrive in time. A returned call is the only success.
/// </summary>
public sealed class RabbitMqMessagePublisher(RabbitMqConnection connection, IOptions<OutboxPublisherOptions> options)
    : IIntegrationMessagePublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;

    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var envelope = ToEnvelope(message);
        var properties = new BasicProperties
        {
            MessageId = message.Id.ToString(),
            Type = message.Type,
            ContentType = IntegrationEventEnvelope.ContentType,
            DeliveryMode = DeliveryModes.Persistent,
            CorrelationId = message.CorrelationId?.ToString(),
            Timestamp = new AmqpTimestamp(message.OccurredAtUtc.ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?> { ["x-event-version"] = message.Version }
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.PublishTimeout);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var channel = await ChannelAsync(timeout.Token);
            await channel.BasicPublishAsync(
                RabbitMqTopology.Exchange, message.RoutingKey, mandatory: false, properties, envelope.ToUtf8Bytes(), timeout.Token);
        }
        catch (Exception) when (_channel is { IsOpen: false })
        {
            _channel = null; // reopened on the next attempt
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The broker message body for an outbox message: MessageId is the outbox id.</summary>
    public static IntegrationEventEnvelope ToEnvelope(OutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.Payload);
        return new IntegrationEventEnvelope(
            message.Id, message.Type, message.Version, message.OccurredAtUtc, message.CorrelationId, payload.RootElement.Clone());
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is { } channel)
        {
            try
            {
                await channel.DisposeAsync();
            }
            catch (Exception)
            {
                // The connection is going away anyway.
            }
        }

        _gate.Dispose();
    }

    private async Task<IChannel> ChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } open)
        {
            return open;
        }

        var opened = await (await connection.GetAsync(cancellationToken)).CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        await RabbitMqTopology.DeclareExchangeAsync(opened, cancellationToken);
        _channel = opened;
        return opened;
    }
}
