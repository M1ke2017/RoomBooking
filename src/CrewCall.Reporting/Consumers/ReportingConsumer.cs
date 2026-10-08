using CrewCall.Contracts.Integration;
using CrewCall.Messaging;
using CrewCall.Reporting.Projections;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace CrewCall.Reporting.Consumers;

/// <summary>
/// Feeds the reporting projections from RabbitMQ (ADR-0016): the crewcall.reporting queue, bound to crewcall.events with
/// exactly the projected routing keys. The broker side (manual ACK after the commit, NACK with requeue on failure,
/// rejecting invalid messages, reconnecting) is the shared <see cref="IntegrationEventConsumer"/>; the projection work
/// is <see cref="IReportingProjectionProcessor"/>.
/// </summary>
public sealed class ReportingConsumer(
    RabbitMqConnection connection, IServiceScopeFactory scopes, IOptions<ReportingOptions> options, ILogger<ReportingConsumer> logger)
    : IntegrationEventConsumer(connection, logger)
{
    private readonly ReportingOptions _options = options.Value;
    private readonly ILogger<ReportingConsumer> _logger = logger;

    protected override string DisplayName => "Reporting consumer";

    protected override string QueueName => _options.QueueName;

    protected override ushort PrefetchCount => _options.PrefetchCount;

    protected override TimeSpan ReconnectDelay => _options.ReconnectDelay;

    protected override TimeSpan RequeueDelay => _options.RequeueDelay;

    protected override Task DeclareQueueAsync(IChannel channel, CancellationToken cancellationToken) =>
        IntegrationExchange.DeclareQueueAsync(channel, _options.QueueName, ReportingEventReader.SubscribedRoutingKeys, cancellationToken);

    protected override async Task HandleAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Reporting received {Type} v{Version} {MessageId}.", envelope.Type, envelope.Version, envelope.MessageId);
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IReportingProjectionProcessor>().ProcessAsync(envelope, cancellationToken);
    }
}
