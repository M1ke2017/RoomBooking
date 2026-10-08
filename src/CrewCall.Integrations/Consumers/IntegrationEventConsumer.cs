using CrewCall.Contracts.Integration;
using CrewCall.Integrations.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace CrewCall.Integrations.Consumers;

/// <summary>
/// A message from which no consumer can ever succeed (e.g. a payload that does not match its contract): it is rejected
/// without requeue instead of being redelivered forever.
/// </summary>
public sealed class PoisonMessageException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// The RabbitMQ side of a consumer of integration events, shared by every consumer (ADR-0014, ADR-0015):
/// - manual acknowledgements: a message is ACKed only after <see cref="HandleAsync"/> returned, i.e. after the consumer's
///   effect succeeded and its inbox record was written (or it was recognized as a duplicate);
/// - a body that is not a valid envelope, or a <see cref="PoisonMessageException"/>, is rejected without requeue;
/// - any other failure (database, SignalR, …) NACKs with requeue after a short pause, so the message is delivered again;
/// - the connection is reopened when it is lost.
/// </summary>
public abstract class IntegrationEventConsumer(RabbitMqConnection connection, ILogger logger) : BackgroundService
{
    /// <summary>The name used in logs, e.g. "Integration audit consumer".</summary>
    protected abstract string DisplayName { get; }

    protected abstract string QueueName { get; }

    protected virtual ushort PrefetchCount => 10;

    protected virtual TimeSpan ReconnectDelay => TimeSpan.FromSeconds(2);

    protected virtual TimeSpan RequeueDelay => TimeSpan.FromSeconds(1);

    /// <summary>Declares the consumer's queue and its bindings (idempotent).</summary>
    protected abstract Task DeclareQueueAsync(IChannel channel, CancellationToken cancellationToken);

    /// <summary>
    /// Handles one valid envelope. Returning means the message may be acknowledged; throwing means it must be delivered
    /// again (or, for a <see cref="PoisonMessageException"/>, dropped).
    /// </summary>
    protected abstract Task HandleAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var channel = await (await connection.GetAsync(stoppingToken)).CreateChannelAsync(cancellationToken: stoppingToken);
                await DeclareQueueAsync(channel, stoppingToken);
                await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: PrefetchCount, global: false, stoppingToken);

                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.ChannelShutdownAsync += (_, _) =>
                {
                    closed.TrySetResult();
                    return Task.CompletedTask;
                };

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, delivery) => HandleDeliveryAsync(channel, delivery, stoppingToken);
                await channel.BasicConsumeAsync(QueueName, autoAck: false, consumer, stoppingToken);
                logger.LogInformation("{Consumer} listening on {Queue}.", DisplayName, QueueName);

                await closed.Task.WaitAsync(stoppingToken);
                logger.LogWarning("{Consumer} channel closed; reconnecting.", DisplayName);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "{Consumer} could not connect; retrying.", DisplayName);
            }

            try
            {
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task HandleDeliveryAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        var envelope = IntegrationEventEnvelope.TryParse(delivery.Body.Span, out var error);
        if (envelope is null || (delivery.BasicProperties.MessageId is { } messageId && messageId != envelope.MessageId.ToString()))
        {
            logger.LogError(
                "Rejected an invalid integration message {MessageId} ({RoutingKey}) in the {Consumer}: {Error}",
                delivery.BasicProperties.MessageId, delivery.RoutingKey, DisplayName, error ?? "MessageId property and envelope differ.");
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, stoppingToken);
            return;
        }

        try
        {
            await HandleAsync(envelope, stoppingToken);

            // Only now, after the effect and the inbox record: the message is safely handled (or was already).
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
        }
        catch (PoisonMessageException poison)
        {
            logger.LogError(poison, "Rejected {Type} {MessageId} in the {Consumer}: {Error}", envelope.Type, envelope.MessageId, DisplayName, poison.Message);
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, stoppingToken);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "{Consumer}: handling {Type} {MessageId} failed; it will be redelivered.", DisplayName, envelope.Type, envelope.MessageId);
            await Task.Delay(RequeueDelay, stoppingToken);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, stoppingToken);
        }
    }
}
