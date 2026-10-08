namespace CrewCall.Contracts.Live;

/// <summary>
/// A live operations notification (ADR-0015), sent to browsers over SignalR. It is a change hint, not state: it says what
/// changed and where, and the client refreshes the affected data from the REST API. It is deliberately small and never
/// carries entities, calendars or history.
/// </summary>
/// <param name="MessageId">The integration message id (= outbox id) it came from. Delivery is at-least-once, so a client
/// may see a message twice and deduplicates by this id.</param>
/// <param name="Type">The live type, e.g. "visit.work.completed" (<see cref="LiveOperationTypes"/>).</param>
/// <param name="OccurredAtUtc">When the business change happened.</param>
/// <param name="EntityType">The entity the change is about (<see cref="LiveEntityTypes"/>).</param>
/// <param name="EntityId">Its id.</param>
/// <param name="Action">What happened to it, e.g. "completed" (<see cref="LiveOperationActions"/>).</param>
/// <param name="CorrelationId">The correlation id of the request that caused the change, if any.</param>
/// <param name="Related">Other entities the change concerns (visit, technician, site, …); they also decide the groups.</param>
/// <param name="Summary">A short human-readable line for developer tooling. Not for parsing.</param>
public sealed record LiveOperationMessage(
    Guid MessageId,
    string Type,
    DateTimeOffset OccurredAtUtc,
    string EntityType,
    Guid EntityId,
    string Action,
    Guid? CorrelationId,
    IReadOnlyList<LiveEntityReference> Related,
    string? Summary);

/// <summary>An entity a live message concerns.</summary>
public sealed record LiveEntityReference(string EntityType, Guid EntityId);

/// <summary>The live message types. Independent of the integration event names and versions.</summary>
public static class LiveOperationTypes
{
    public const string AssignmentCreated = "assignment.created";
    public const string AssignmentReplaced = "assignment.replaced";
    public const string AssignmentCancelled = "assignment.cancelled";
    public const string IncidentDispatched = "incident.dispatched";
    public const string VisitWorkCompleted = "visit.work.completed";

    public static IReadOnlyList<string> All { get; } =
        [AssignmentCreated, AssignmentReplaced, AssignmentCancelled, IncidentDispatched, VisitWorkCompleted];
}

public static class LiveEntityTypes
{
    public const string Assignment = "assignment";
    public const string Incident = "incident";
    public const string Visit = "visit";
    public const string WorkOrder = "workOrder";
    public const string Execution = "execution";
    public const string Technician = "technician";
    public const string Site = "site";
}

public static class LiveOperationActions
{
    public const string Created = "created";
    public const string Replaced = "replaced";
    public const string Cancelled = "cancelled";
    public const string Dispatched = "dispatched";
    public const string Completed = "completed";
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
