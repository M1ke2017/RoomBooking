using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrewCall.Contracts.Integration;

/// <param name="Type">The stable public name, e.g. "visit.work-completed". Never a .NET type name.</param>
/// <param name="Version">The contract version; starts at 1.</param>
/// <param name="RoutingKey">The topic routing key on the crewcall.events exchange.</param>
public sealed record IntegrationEventDescriptor(string Type, int Version, string RoutingKey);

/// <summary>
/// The explicit table of published integration events: their public type, version and routing key. Adding an event means
/// adding a line here (no reflection-based discovery).
/// </summary>
public static class IntegrationEventCatalog
{
    public const string Exchange = "crewcall.events";

    public static readonly IntegrationEventDescriptor AssignmentCreated = new("assignment.created", 1, "assignment.created");
    public static readonly IntegrationEventDescriptor AssignmentReplaced = new("assignment.replaced", 1, "assignment.replaced");
    public static readonly IntegrationEventDescriptor AssignmentCancelled = new("assignment.cancelled", 1, "assignment.cancelled");
    public static readonly IntegrationEventDescriptor IncidentDispatched = new("incident.dispatched", 1, "incident.dispatched");
    public static readonly IntegrationEventDescriptor VisitWorkCompleted = new("visit.work-completed", 1, "visit.work.completed");
    public static readonly IntegrationEventDescriptor VisitCreated = new("visit.created", 1, "visit.created");
    public static readonly IntegrationEventDescriptor VisitStatusChanged = new("visit.status-changed", 1, "visit.status.changed");

    public static IReadOnlyList<IntegrationEventDescriptor> All { get; } =
        [AssignmentCreated, AssignmentReplaced, AssignmentCancelled, IncidentDispatched, VisitWorkCompleted, VisitCreated, VisitStatusChanged];

    /// <summary>JSON for payloads and envelopes: camelCase, enums as strings.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static IntegrationEventDescriptor Describe(IIntegrationEvent integrationEvent) => integrationEvent switch
    {
        AssignmentCreatedIntegrationEvent => AssignmentCreated,
        AssignmentReplacedIntegrationEvent => AssignmentReplaced,
        AssignmentCancelledIntegrationEvent => AssignmentCancelled,
        IncidentDispatchedIntegrationEvent => IncidentDispatched,
        VisitWorkCompletedIntegrationEvent => VisitWorkCompleted,
        VisitCreatedIntegrationEvent => VisitCreated,
        VisitStatusChangedIntegrationEvent => VisitStatusChanged,
        _ => throw new ArgumentException($"{integrationEvent.GetType().Name} is not a published integration event.", nameof(integrationEvent))
    };

    /// <summary>The routing key of a published type, e.g. "visit.work-completed" → "visit.work.completed".</summary>
    public static string RoutingKeyFor(string type) =>
        All.SingleOrDefault(descriptor => descriptor.Type == type)?.RoutingKey
        ?? throw new ArgumentException($"'{type}' is not a published integration event type.", nameof(type));

    /// <summary>The event's payload JSON (its own fields, camelCase), as stored in the outbox and sent in the envelope.</summary>
    public static string SerializePayload(IIntegrationEvent integrationEvent) =>
        JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), JsonOptions);
}
