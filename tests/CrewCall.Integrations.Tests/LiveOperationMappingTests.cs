using System.Text.Json;
using CrewCall.Contracts.Integration;
using CrewCall.Contracts.Live;
using CrewCall.Integrations.Consumers;
using CrewCall.Integrations.Live;
using Xunit;
using static CrewCall.Integrations.Tests.LiveTestKit;

namespace CrewCall.Integrations.Tests;

/// <summary>The explicit integration event → live message mapping (ADR-0015).</summary>
public sealed class LiveOperationMappingTests
{
    private static readonly Guid Correlation = Guid.NewGuid();
    private static readonly Guid SiteId = Guid.NewGuid();
    private static readonly Guid VisitId = Guid.NewGuid();
    private static readonly Guid TechnicianId = Guid.NewGuid();

    private static LiveOperationMessage MapThrough(IIntegrationEvent integrationEvent, LiveRoutingContext routing)
    {
        var envelope = Envelope(integrationEvent);
        var read = LiveOperationMapper.ReadEvent(envelope);
        Assert.Equal(integrationEvent, read, new EventComparer());
        return LiveOperationMapper.Map(envelope, read!, routing);
    }

    private static void AssertRelated(LiveOperationMessage message, params (string Type, Guid Id)[] expected) =>
        Assert.Equal(
            expected.Select(e => new LiveEntityReference(e.Type, e.Id)).OrderBy(r => r.EntityType).ThenBy(r => r.EntityId),
            message.Related.OrderBy(r => r.EntityType).ThenBy(r => r.EntityId));

    [Fact]
    public void Assignment_created_maps_to_an_assignment_created_message_about_the_assignment()
    {
        var created = new AssignmentCreatedIntegrationEvent(Guid.NewGuid(), OccurredAt, Correlation, Guid.NewGuid(), VisitId, TechnicianId, null, []);

        var message = MapThrough(created, new LiveRoutingContext(SiteId, []));

        Assert.Equal(
            (created.EventId, "assignment.created", OccurredAt, "assignment", created.AssignmentId, "created", (Guid?)Correlation),
            (message.MessageId, message.Type, message.OccurredAtUtc, message.EntityType, message.EntityId, message.Action, message.CorrelationId));
        AssertRelated(message, ("visit", VisitId), ("technician", TechnicianId), ("site", SiteId));
    }

    [Fact]
    public void Assignment_replaced_maps_to_the_new_assignment_and_tells_both_technicians()
    {
        var replaced = new AssignmentReplacedIntegrationEvent(Guid.NewGuid(), OccurredAt, null, Guid.NewGuid(), Guid.NewGuid(), VisitId);
        var newTechnician = Guid.NewGuid();

        var message = MapThrough(replaced, new LiveRoutingContext(SiteId, [TechnicianId, newTechnician]));

        Assert.Equal(("assignment.replaced", "assignment", replaced.NewAssignmentId, "replaced"),
            (message.Type, message.EntityType, message.EntityId, message.Action));
        AssertRelated(message, ("assignment", replaced.OldAssignmentId), ("visit", VisitId),
            ("technician", TechnicianId), ("technician", newTechnician), ("site", SiteId));
    }

    [Fact]
    public void Assignment_cancelled_maps_to_an_assignment_cancelled_message()
    {
        var cancelled = new AssignmentCancelledIntegrationEvent(Guid.NewGuid(), OccurredAt, null, Guid.NewGuid(), VisitId);

        var message = MapThrough(cancelled, new LiveRoutingContext(SiteId, [TechnicianId]));

        Assert.Equal(("assignment.cancelled", "assignment", cancelled.AssignmentId, "cancelled"),
            (message.Type, message.EntityType, message.EntityId, message.Action));
        AssertRelated(message, ("visit", VisitId), ("technician", TechnicianId), ("site", SiteId));
    }

    [Fact]
    public void Incident_dispatched_maps_to_an_incident_message_with_its_work_order_visit_assignment_and_technician()
    {
        var dispatched = new IncidentDispatchedIntegrationEvent(
            Guid.NewGuid(), OccurredAt, Correlation, Guid.NewGuid(), Guid.NewGuid(), VisitId, Guid.NewGuid(), TechnicianId);

        var message = MapThrough(dispatched, new LiveRoutingContext(SiteId, []));

        Assert.Equal(("incident.dispatched", "incident", dispatched.IncidentId, "dispatched", (Guid?)Correlation),
            (message.Type, message.EntityType, message.EntityId, message.Action, message.CorrelationId));
        AssertRelated(message, ("workOrder", dispatched.WorkOrderId), ("visit", VisitId), ("assignment", dispatched.AssignmentId),
            ("technician", TechnicianId), ("site", SiteId));
    }

    [Fact]
    public void Visit_work_completed_maps_to_the_live_type_visit_work_completed()
    {
        var completed = new VisitWorkCompletedIntegrationEvent(Guid.NewGuid(), OccurredAt, null, VisitId, Guid.NewGuid(), 12m, 60m, 5m, 55m);

        var message = MapThrough(completed, new LiveRoutingContext(SiteId, [TechnicianId]));

        // The integration type is "visit.work-completed"; the live type is the routing-key-style "visit.work.completed".
        Assert.Equal(("visit.work.completed", "visit", VisitId, "completed"), (message.Type, message.EntityType, message.EntityId, message.Action));
        AssertRelated(message, ("execution", completed.ExecutionId), ("technician", TechnicianId), ("site", SiteId));
    }

    [Fact]
    public void The_message_id_and_correlation_id_are_the_envelopes_unchanged()
    {
        var created = new AssignmentCreatedIntegrationEvent(Guid.NewGuid(), OccurredAt, Correlation, Guid.NewGuid(), VisitId, TechnicianId, null, []);
        var envelope = Envelope(created);

        var first = LiveOperationMapper.Map(envelope, LiveOperationMapper.ReadEvent(envelope)!, LiveRoutingContext.None);
        var second = LiveOperationMapper.Map(envelope, LiveOperationMapper.ReadEvent(envelope)!, LiveRoutingContext.None);

        Assert.Equal(envelope.MessageId, first.MessageId);
        Assert.Equal(first.MessageId, second.MessageId);
        Assert.Equal(Correlation, first.CorrelationId);
    }

    [Fact]
    public void Without_routing_context_the_message_only_carries_what_the_event_says()
    {
        var completed = new VisitWorkCompletedIntegrationEvent(Guid.NewGuid(), OccurredAt, null, VisitId, Guid.NewGuid(), null, 60m, 0m, 60m);

        var message = MapThrough(completed, LiveRoutingContext.None);

        AssertRelated(message, ("execution", completed.ExecutionId));
        Assert.Equal([LiveOperationGroups.All], LiveOperationGroups.For(message));
    }

    [Theory]
    [InlineData("assignment.created", 2)]
    [InlineData("visit.work-completed", 2)]
    [InlineData("visit.work.completed", 1)]
    [InlineData("technician.created", 1)]
    public void An_unsupported_type_or_version_is_not_mapped(string type, int version)
    {
        var created = new AssignmentCreatedIntegrationEvent(Guid.NewGuid(), OccurredAt, null, Guid.NewGuid(), VisitId, TechnicianId, null, []);
        var envelope = Envelope(created) with { Type = type, Version = version };

        Assert.Null(LiveOperationMapper.ReadEvent(envelope));
    }

    [Fact]
    public void A_payload_that_does_not_match_its_contract_or_message_id_is_poison()
    {
        var created = new AssignmentCreatedIntegrationEvent(Guid.NewGuid(), OccurredAt, null, Guid.NewGuid(), VisitId, TechnicianId, null, []);
        var envelope = Envelope(created);

        Assert.Throws<PoisonMessageException>(() => LiveOperationMapper.ReadEvent(envelope with { MessageId = Guid.NewGuid() }));
        using var wrongShape = JsonDocument.Parse("""{"eventId":"not-a-guid"}""");
        Assert.Throws<PoisonMessageException>(() => LiveOperationMapper.ReadEvent(envelope with { Payload = wrongShape.RootElement.Clone() }));
    }

    [Fact]
    public void An_event_without_a_live_mapping_is_refused_rather_than_mapped()
    {
        var envelope = Envelope(new AssignmentCancelledIntegrationEvent(Guid.NewGuid(), OccurredAt, null, Guid.NewGuid(), VisitId));

        Assert.Throws<ArgumentException>(() => LiveOperationMapper.Map(envelope, new UnknownEvent(envelope.MessageId), LiveRoutingContext.None));
    }

    [Fact]
    public void The_live_queue_is_bound_to_exactly_the_five_supported_routing_keys()
    {
        Assert.Equal(
            ["assignment.created", "assignment.replaced", "assignment.cancelled", "incident.dispatched", "visit.work.completed"],
            LiveOperationMapper.SupportedRoutingKeys);
        Assert.Equal(LiveOperationTypes.All.Count, LiveOperationMapper.SupportedRoutingKeys.Count);
    }

    [Fact]
    public void A_live_message_is_small_and_never_carries_an_entity_or_integration_payload()
    {
        var properties = typeof(LiveOperationMessage).GetProperties().ToDictionary(p => p.Name, p => p.PropertyType);

        Assert.Equal(
            ["MessageId", "Type", "OccurredAtUtc", "EntityType", "EntityId", "Action", "CorrelationId", "Related", "Summary"],
            properties.Keys);
        Assert.All(properties.Values, type => Assert.True(
            type.Namespace is "System" or "System.Collections.Generic" || type == typeof(IReadOnlyList<LiveEntityReference>), type.FullName));
    }

    private sealed record UnknownEvent(Guid EventId) : IIntegrationEvent
    {
        public DateTimeOffset OccurredAtUtc => OccurredAt;

        public Guid? CorrelationId => null;
    }

    // Records with list fields compare lists by reference; compare their JSON instead.
    private sealed class EventComparer : IEqualityComparer<IIntegrationEvent?>
    {
        public bool Equals(IIntegrationEvent? x, IIntegrationEvent? y) =>
            x is not null && y is not null && IntegrationEventCatalog.SerializePayload(x) == IntegrationEventCatalog.SerializePayload(y);

        public int GetHashCode(IIntegrationEvent? obj) => 0;
    }
}
