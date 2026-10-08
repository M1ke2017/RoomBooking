namespace CrewCall.Contracts.Live;

/// <summary>
/// A live operations notification (ADR-0015, simplified in Sprint 15, ADR-0017): "entity X changed". It is a change
/// hint, not state, and carries no copy of the visit, assignment or incident: the client refreshes the affected data
/// from the REST API. Which groups receive it is decided on the server and is not part of the message.
/// </summary>
/// <param name="MessageId">The integration message id (= outbox id) it came from. Delivery is at-least-once, so a client
/// may see a message twice and deduplicates by this id.</param>
/// <param name="Type">What changed, e.g. "visit.rescheduled" (<see cref="LiveOperationTypes"/>); its first part names the
/// kind of entity.</param>
/// <param name="EntityId">The entity that changed: the assignment, incident or visit.</param>
/// <param name="OccurredAtUtc">When the business change happened.</param>
/// <param name="CorrelationId">The correlation id of the request that caused the change, if any.</param>
public sealed record LiveOperationMessage(Guid MessageId, string Type, Guid EntityId, DateTimeOffset OccurredAtUtc, Guid? CorrelationId);

/// <summary>The live message types. Independent of the integration event names and versions.</summary>
public static class LiveOperationTypes
{
    public const string AssignmentCreated = "assignment.created";
    public const string AssignmentReplaced = "assignment.replaced";
    public const string AssignmentCancelled = "assignment.cancelled";
    public const string IncidentDispatched = "incident.dispatched";
    public const string VisitWorkCompleted = "visit.work.completed";
    public const string VisitRescheduled = "visit.rescheduled";

    public static IReadOnlyList<string> All { get; } =
        [AssignmentCreated, AssignmentReplaced, AssignmentCancelled, IncidentDispatched, VisitWorkCompleted, VisitRescheduled];
}

/// <summary>The SignalR surface shared by the hub and its clients.</summary>
public static class LiveOperationsHubContract
{
    public const string Path = "/hubs/live-operations";

    /// <summary>The single client method: every live message arrives through it.</summary>
    public const string ReceiveOperation = "ReceiveOperation";

    public const string SubscribeAll = "SubscribeAll";
    public const string SubscribeSite = "SubscribeSite";
    public const string SubscribeTechnician = "SubscribeTechnician";
    public const string SubscribeIncident = "SubscribeIncident";
    public const string UnsubscribeAll = "UnsubscribeAll";
    public const string UnsubscribeSite = "UnsubscribeSite";
    public const string UnsubscribeTechnician = "UnsubscribeTechnician";
    public const string UnsubscribeIncident = "UnsubscribeIncident";
}
