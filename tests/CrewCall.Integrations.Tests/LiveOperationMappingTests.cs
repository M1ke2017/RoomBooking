using System.Text.Json;
using CrewCall.Contracts.Integration;
using CrewCall.Contracts.Live;
using CrewCall.Integrations.Live;
using CrewCall.Messaging;
using Xunit;
using static CrewCall.Integrations.Tests.LiveTestKit;

namespace CrewCall.Integrations.Tests;

/// <summary>
/// The explicit integration event → live message mapping (ADR-0015). Since Sprint 15 (ADR-0017) the message is a change
/// hint only: type, the entity that changed, time and ids; the groups are routing, decided on the server.
/// </summary>
public sealed class LiveOperationMappingTests
{
    private static readonly Guid Correlation = Guid.NewGuid();
    private static readonly Guid VisitId = Guid.NewGuid();
    private static readonly Guid TechnicianId = Guid.NewGuid();

    private static LiveOperationMessage MapThrough(IIntegrationEvent integrationEvent)
    {
        var envelope = Envelope(integrationEvent);
        var read = LiveOperationMapper.ReadEvent(envelope);
        Assert.Equal(IntegrationEventCatalog.SerializePayload(integrationEvent), IntegrationEventCatalog.SerializePayload(read!));
        return LiveOperationMapper.Map(envelope, read!);
    }

    public static TheoryData<IIntegrationEvent, string, Guid> Events()
    {
        var assignment = Guid.NewGuid();
        var newAssignment = Guid.NewGuid();
        var incident = Guid.NewGuid();
        return new()
        {
            { new AssignmentCreatedIntegrationEvent(Guid.NewGuid(), OccurredAt, Correlation, assignment, VisitId, TechnicianId, null, []), "assignment.created", assignment },
            { new AssignmentReplacedIntegrationEvent(Guid.NewGuid(), OccurredAt, null, assignment, newAssignment, VisitId), "assignment.replaced", newAssignment },
            { new AssignmentCancelledIntegrationEvent(Guid.NewGuid(), OccurredAt, null, assignment, VisitId), "assignment.cancelled", assignment },
            { new IncidentDispatchedIntegrationEvent(Guid.NewGuid(), OccurredAt, Correlation, incident, Guid.NewGuid(), VisitId, assignment, TechnicianId), "incident.dispatched", incident },
            { new VisitWorkCompletedIntegrationEvent(Guid.NewGuid(), OccurredAt, null, VisitId, Guid.NewGuid(), 12m, 60m, 5m, 55m), "visit.work.completed", VisitId },
            { new VisitRescheduledIntegrationEvent(Guid.NewGuid(), OccurredAt, null, VisitId, OccurredAt, OccurredAt.AddHours(1), OccurredAt.AddHours(4), OccurredAt.AddHours(5), "UrgentIncident", incident), "visit.rescheduled", VisitId }
        };
    }

    [Theory]
    [MemberData(nameof(Events))]
    public void Each_supported_event_maps_to_its_live_type_and_the_entity_that_changed(IIntegrationEvent integrationEvent, string type, Guid entityId)
    {
        var message = MapThrough(integrationEvent);

        Assert.Equal(
            new LiveOperationMessage(integrationEvent.EventId, type, entityId, OccurredAt, integrationEvent.CorrelationId),
            message);
    }

    [Fact]
    public void The_message_id_and_correlation_id_are_the_envelopes_unchanged()
    {
        var created = new AssignmentCreatedIntegrationEvent(Guid.NewGuid(), OccurredAt, Correlation, Guid.NewGuid(), VisitId, TechnicianId, null, []);
        var envelope = Envelope(created);

        var first = LiveOperationMapper.Map(envelope, LiveOperationMapper.ReadEvent(envelope)!);
        var second = LiveOperationMapper.Map(envelope, LiveOperationMapper.ReadEvent(envelope)!);

        Assert.Equal(envelope.MessageId, first.MessageId);
        Assert.Equal(first, second);
        Assert.Equal(Correlation, first.CorrelationId);
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

        Assert.Throws<ArgumentException>(() => LiveOperationMapper.Map(envelope, new UnknownEvent(envelope.MessageId)));
    }

    [Fact]
    public void The_live_queue_is_bound_to_exactly_the_supported_routing_keys()
    {
        Assert.Equal(
            ["assignment.created", "assignment.replaced", "assignment.cancelled", "incident.dispatched", "visit.work.completed", "visit.rescheduled"],
            LiveOperationMapper.SupportedRoutingKeys);
        Assert.Equal(LiveOperationTypes.All.Count, LiveOperationMapper.SupportedRoutingKeys.Count);
    }

    [Fact]
    public void A_live_message_is_only_a_change_hint_and_never_carries_a_copy_of_the_entity()
    {
        Assert.Equal(
            ["MessageId", "Type", "EntityId", "OccurredAtUtc", "CorrelationId"],
            typeof(LiveOperationMessage).GetProperties().Select(p => p.Name));
    }

    private sealed record UnknownEvent(Guid EventId) : IIntegrationEvent
    {
        public DateTimeOffset OccurredAtUtc => OccurredAt;

        public Guid? CorrelationId => null;
    }
}
