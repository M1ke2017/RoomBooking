using CrewCall.Contracts.Integration;
using CrewCall.Integrations.Messaging;
using CrewCall.Persistence.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace CrewCall.Integrations.Consumers;

/// <summary>
/// Consumes every integration event from the crewcall.integration-audit queue (bound with "#") with manual
/// acknowledgements (ADR-0014):
/// - a message is ACKed only after its inbox entry and receipt were committed (or it was recognized as a duplicate);
/// - a message that is not a valid envelope is rejected without requeue (it can never succeed);
/// - a transient failure (e.g. the database) NACKs with requeue after a short pause, so it is delivered again.
/// The connection is reopened when it is lost.
/// </summary>
public sealed class IntegrationAuditConsumer(
    RabbitMqConnection connection, IntegrationAuditHandler handler, ILogger<IntegrationAuditConsumer> logger) : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequeueDelay = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var channel = await (await connection.GetAsync(stoppingToken)).CreateChannelAsync(cancellationToken: stoppingToken);
                await RabbitMqTopology.DeclareAuditQueueAsync(channel, stoppingToken);
                await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 10, global: false, stoppingToken);

                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.ChannelShutdownAsync += (_, _) =>
                {
                    closed.TrySetResult();
                    return Task.CompletedTask;
                };

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, delivery) => HandleDeliveryAsync(channel, delivery, stoppingToken);
                await channel.BasicConsumeAsync(RabbitMqTopology.AuditQueue, autoAck: false, consumer, stoppingToken);
                logger.LogInformation("Integration audit consumer listening on {Queue}.", RabbitMqTopology.AuditQueue);

                await closed.Task.WaitAsync(stoppingToken);
                logger.LogWarning("Integration audit consumer channel closed; reconnecting.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Integration audit consumer could not connect; retrying.");
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
                "Rejected an invalid integration message {MessageId} ({RoutingKey}): {Error}",
                delivery.BasicProperties.MessageId, delivery.RoutingKey, error ?? "MessageId property and envelope differ.");
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, stoppingToken);
            return;
        }

        try
        {
            var result = await handler.HandleAsync(envelope, stoppingToken);
            if (result == InboxResult.Duplicate)
            {
                logger.LogInformation("Duplicate {Type} {MessageId} skipped.", envelope.Type, envelope.MessageId);
            }
            else
            {
                logger.LogInformation("Received {Type} v{Version} {MessageId}.", envelope.Type, envelope.Version, envelope.MessageId);
            }

            // Only now, after the commit: the message is safely recorded (or was already).
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Handling {Type} {MessageId} failed; it will be redelivered.", envelope.Type, envelope.MessageId);
            await Task.Delay(RequeueDelay, stoppingToken);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, stoppingToken);
        }
    }
}
