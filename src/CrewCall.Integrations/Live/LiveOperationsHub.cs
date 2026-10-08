using Microsoft.AspNetCore.SignalR;

namespace CrewCall.Integrations.Live;

/// <summary>
/// The live operations hub (/hubs/live-operations, ADR-0015). A delivery channel only: it has no business logic, keeps
/// no state and is never asked for data. Clients choose what to hear about by subscribing to groups; every message
/// arrives through the single client method "ReceiveOperation".
/// Group names are built on the server from typed ids (<see cref="LiveOperationGroups"/>); no method takes a group name.
/// There is no authorization yet: any connected client may subscribe to any group (a known issue, ADR-0015).
/// </summary>
public sealed class LiveOperationsHub(ILogger<LiveOperationsHub> logger) : Hub
{
    public override Task OnConnectedAsync()
    {
        logger.LogInformation("Live operations connection {ConnectionId} opened.", Context.ConnectionId);
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // SignalR removes the connection from all of its groups.
        logger.LogInformation("Live operations connection {ConnectionId} closed.", Context.ConnectionId);
        return Task.CompletedTask;
    }

    public Task SubscribeAll() => JoinAsync(LiveOperationGroups.All, "all", null);

    public Task SubscribeSite(Guid siteId) => JoinAsync(Group(LiveOperationGroups.Site, siteId), "site", siteId);

    public Task SubscribeTechnician(Guid technicianId) =>
        JoinAsync(Group(LiveOperationGroups.Technician, technicianId), "technician", technicianId);

    public Task SubscribeIncident(Guid incidentId) => JoinAsync(Group(LiveOperationGroups.Incident, incidentId), "incident", incidentId);

    public Task UnsubscribeAll() => LeaveAsync(LiveOperationGroups.All, "all", null);

    public Task UnsubscribeSite(Guid siteId) => LeaveAsync(Group(LiveOperationGroups.Site, siteId), "site", siteId);

    public Task UnsubscribeTechnician(Guid technicianId) =>
        LeaveAsync(Group(LiveOperationGroups.Technician, technicianId), "technician", technicianId);

    public Task UnsubscribeIncident(Guid incidentId) => LeaveAsync(Group(LiveOperationGroups.Incident, incidentId), "incident", incidentId);

    // An invalid id becomes a HubException, whose message the client sees.
    private static string Group(Func<Guid, string> build, Guid id)
    {
        try
        {
            return build(id);
        }
        catch (ArgumentException exception)
        {
            throw new HubException(exception.Message);
        }
    }

    private async Task JoinAsync(string group, string subscriptionType, Guid? subscriptionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        logger.LogInformation(
            "Live operations connection {ConnectionId} subscribed to {SubscriptionType} {SubscriptionId}.",
            Context.ConnectionId, subscriptionType, subscriptionId);
    }

    private async Task LeaveAsync(string group, string subscriptionType, Guid? subscriptionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        logger.LogInformation(
            "Live operations connection {ConnectionId} unsubscribed from {SubscriptionType} {SubscriptionId}.",
            Context.ConnectionId, subscriptionType, subscriptionId);
    }
}
