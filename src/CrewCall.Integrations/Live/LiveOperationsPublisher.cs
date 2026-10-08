using CrewCall.Contracts.Live;
using Microsoft.AspNetCore.SignalR;

namespace CrewCall.Integrations.Live;

/// <summary>
/// Sends a live message to the clients that subscribed to it. The live consumer depends on this abstraction, not on the
/// hub; business modules never see either (ADR-0015).
/// </summary>
public interface ILiveOperationsPublisher
{
    /// <summary>
    /// Sends <paramref name="message"/> to <paramref name="groups"/> (<see cref="LiveOperationGroups.For"/>). Completing
    /// means the broadcast was handed to SignalR; sending to groups nobody joined is a success. Throws when it could not
    /// be sent.
    /// </summary>
    Task PublishAsync(LiveOperationMessage message, IReadOnlyList<string> groups, CancellationToken cancellationToken);
}

/// <summary>The SignalR implementation: <see cref="IHubContext{THub}"/> of <see cref="LiveOperationsHub"/>.</summary>
public sealed class SignalRLiveOperationsPublisher(IHubContext<LiveOperationsHub> hub, ILogger<SignalRLiveOperationsPublisher> logger)
    : ILiveOperationsPublisher
{
    public async Task PublishAsync(LiveOperationMessage message, IReadOnlyList<string> groups, CancellationToken cancellationToken)
    {
        try
        {
            await hub.Clients.Groups(groups).SendAsync(LiveOperationsHubContract.ReceiveOperation, message, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "SignalR publish of live {Type} {MessageId} failed.", message.Type, message.MessageId);
            throw;
        }

        logger.LogInformation(
            "Live {Type} {MessageId} published to {Groups}.", message.Type, message.MessageId, string.Join(", ", groups));
    }
}
