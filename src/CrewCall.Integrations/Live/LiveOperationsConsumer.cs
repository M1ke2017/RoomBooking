using CrewCall.Contracts.Integration;
using CrewCall.Messaging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace CrewCall.Integrations.Live;

/// <summary>
/// Turns integration events into live notifications (ADR-0015): consumes the crewcall.live-operations queue, bound to
/// crewcall.events with the supported routing keys, and hands each message to <see cref="LiveOperationsHandler"/>.
/// A message is ACKed only after it was broadcast and recorded in this consumer's inbox; a SignalR or database failure
/// NACKs it with requeue (<see cref="IntegrationEventConsumer"/>). Nobody being connected is not a failure.
/// </summary>
public sealed class LiveOperationsConsumer(
    RabbitMqConnection connection, LiveOperationsHandler handler, IOptions<LiveOperationsOptions> options, ILogger<LiveOperationsConsumer> logger)
    : IntegrationEventConsumer(connection, logger)
{
    private readonly LiveOperationsOptions _options = options.Value;
    private readonly ILogger<LiveOperationsConsumer> _logger = logger;

    protected override string DisplayName => "Live operations consumer";

    protected override string QueueName => _options.QueueName;

    protected override ushort PrefetchCount => _options.PrefetchCount;

    protected override TimeSpan ReconnectDelay => _options.ReconnectDelay;

    protected override TimeSpan RequeueDelay => _options.RequeueDelay;

    protected override Task DeclareQueueAsync(IChannel channel, CancellationToken cancellationToken) =>
        IntegrationExchange.DeclareQueueAsync(channel, _options.QueueName, LiveOperationMapper.SupportedRoutingKeys, cancellationToken);

    protected override async Task HandleAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken)
    {
        switch (await handler.HandleAsync(envelope, cancellationToken))
        {
            case LiveDeliveryResult.Duplicate:
                _logger.LogInformation("Duplicate live {Type} {MessageId} skipped.", envelope.Type, envelope.MessageId);
                break;
            case LiveDeliveryResult.Unsupported:
                _logger.LogWarning("Ignored {Type} v{Version} {MessageId}: no live mapping.", envelope.Type, envelope.Version, envelope.MessageId);
                break;
        }
    }
}
