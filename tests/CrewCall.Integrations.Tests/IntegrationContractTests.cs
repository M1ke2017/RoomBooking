using System.Text;
using System.Text.Json;
using CrewCall.Contracts.Integration;
using CrewCall.Persistence.Messaging;
using CrewCall.Scheduling.Assignments;
using CrewCall.WorkOrders;
using CrewCall.WorkOrders.Operations;
using CrewCall.WorkOrders.Visits;
using Xunit;

namespace CrewCall.Integrations.Tests;

/// <summary>The published contracts (ADR-0014, ADR-0016): stable names, versions, explicit fields, a validated envelope.</summary>
public sealed class IntegrationContractTests
{
    private static readonly DateTimeOffset At = new(2038, 3, 1, 9, 30, 0, TimeSpan.Zero);

    public static TheoryData<IIntegrationEvent, string, string, string[]> Events() => new()
    {
        { new AssignmentCreatedIntegrationEvent(Guid.NewGuid(), At, null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, [Guid.NewGuid()]),
          "assignment.created", "assignment.created",
          ["eventId", "occurredAtUtc", "correlationId", "assignmentId", "visitId", "technicianId", "vehicleId", "equipmentIds"] },
        { new AssignmentReplacedIntegrationEvent(Guid.NewGuid(), At, null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
          "assignment.replaced", "assignment.replaced",
          ["eventId", "occurredAtUtc", "correlationId", "oldAssignmentId", "newAssignmentId", "visitId"] },
        { new AssignmentCancelledIntegrationEvent(Guid.NewGuid(), At, null, Guid.NewGuid(), Guid.NewGuid()),
          "assignment.cancelled", "assignment.cancelled",
          ["eventId", "occurredAtUtc", "correlationId", "assignmentId", "visitId"] },
        { new IncidentDispatchedIntegrationEvent(Guid.NewGuid(), At, null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
          "incident.dispatched", "incident.dispatched",
          ["eventId", "occurredAtUtc", "correlationId", "incidentId", "workOrderId", "visitId", "assignmentId", "technicianId"] },
        { new VisitWorkCompletedIntegrationEvent(Guid.NewGuid(), At, null, Guid.NewGuid(), Guid.NewGuid(), 30m, 120m, 15m, 105m),
          "visit.work-completed", "visit.work.completed",
          ["eventId", "occurredAtUtc", "correlationId", "visitId", "executionId", "travelMinutes", "grossWorkMinutes", "pauseMinutes", "netWorkMinutes"] },
        { new VisitCreatedIntegrationEvent(Guid.NewGuid(), At, null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), At.AddDays(1), At.AddDays(1).AddHours(1), At, "High"),
          "visit.created", "visit.created",
          ["eventId", "occurredAtUtc", "correlationId", "visitId", "workOrderId", "customerId", "siteId", "plannedStartUtc", "plannedEndUtc", "createdAtUtc", "workOrderPriority"] },
        { new VisitStatusChangedIntegrationEvent(Guid.NewGuid(), At, null, Guid.NewGuid(), "Planned", "InProgress"),
          "visit.status-changed", "visit.status.changed",
          ["eventId", "occurredAtUtc", "correlationId", "visitId", "oldStatus", "newStatus"] },
        { new VisitRescheduledIntegrationEvent(Guid.NewGuid(), At, null, Guid.NewGuid(), At.AddHours(1), At.AddHours(2), At.AddHours(4), At.AddHours(5), "UrgentIncident", Guid.NewGuid()),
          "visit.rescheduled", "visit.rescheduled",
          ["eventId", "occurredAtUtc", "correlationId", "visitId", "oldStartUtc", "oldEndUtc", "newStartUtc", "newEndUtc", "reason", "incidentId"] },
    };

    [Theory]
    [MemberData(nameof(Events))]
    public void Each_event_has_a_stable_type_version_1_an_explicit_routing_key_and_exactly_its_contract_fields(
        IIntegrationEvent integrationEvent, string type, string routingKey, string[] fields)
    {
        var descriptor = IntegrationEventCatalog.Describe(integrationEvent);
        Assert.Equal((type, 1, routingKey), (descriptor.Type, descriptor.Version, descriptor.RoutingKey));
        Assert.NotEqual(integrationEvent.GetType().Name, descriptor.Type); // never the .NET name
        Assert.Equal(routingKey, IntegrationEventCatalog.RoutingKeyFor(type));

        // The payload is exactly the contract: camelCase fields, nothing else (no EF internals, no navigation, no type info).
        using var payload = JsonDocument.Parse(IntegrationEventCatalog.SerializePayload(integrationEvent));
        Assert.Equal(fields.Order(), payload.RootElement.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(integrationEvent.EventId, payload.RootElement.GetProperty("eventId").GetGuid());
        Assert.Equal(At, payload.RootElement.GetProperty("occurredAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public void The_catalog_lists_exactly_the_published_events_with_unique_types()
    {
        Assert.Equal(
            ["assignment.cancelled", "assignment.created", "assignment.replaced", "incident.dispatched", "visit.created", "visit.rescheduled", "visit.status-changed", "visit.work-completed"],
            IntegrationEventCatalog.All.Select(descriptor => descriptor.Type).Order());
        Assert.All(IntegrationEventCatalog.All, descriptor => Assert.Equal(1, descriptor.Version));
        Assert.Equal(IntegrationEventCatalog.All.Count, IntegrationEventCatalog.All.Select(d => d.RoutingKey).Distinct().Count());
        Assert.Equal("crewcall.events", IntegrationEventCatalog.Exchange);
    }

    [Fact]
    public void The_envelope_round_trips_and_keeps_the_message_id()
    {
        var messageId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        using var payload = JsonDocument.Parse("""{"visitId":"0c8a8a0e-6d3f-4c55-9a5f-3f0b2a1c9d11","netWorkMinutes":105}""");
        var envelope = new IntegrationEventEnvelope(messageId, "visit.work-completed", 1, At, correlationId, payload.RootElement.Clone());

        var bytes = envelope.ToUtf8Bytes();
        using var json = JsonDocument.Parse(bytes);
        var parsed = IntegrationEventEnvelope.TryParse(bytes, out var error);

        Assert.Null(error);
        Assert.Equal(["correlationId", "messageId", "occurredAtUtc", "payload", "type", "version"],
            json.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal((messageId, "visit.work-completed", 1, At, (Guid?)correlationId),
            (parsed!.MessageId, parsed.Type, parsed.Version, parsed.OccurredAtUtc, parsed.CorrelationId));
        Assert.Equal(105, parsed.Payload.GetProperty("netWorkMinutes").GetInt32());
        Assert.Equal("application/json", IntegrationEventEnvelope.ContentType);
    }

    [Theory]
    [InlineData("not json", "Not a JSON envelope")]
    [InlineData("""{"messageId":"00000000-0000-0000-0000-000000000000","type":"a","version":1,"occurredAtUtc":"2038-01-01T00:00:00Z","payload":{}}""", "MessageId")]
    [InlineData("""{"messageId":"7d0f7d0f-0000-0000-0000-000000000001","type":" ","version":1,"occurredAtUtc":"2038-01-01T00:00:00Z","payload":{}}""", "Type")]
    [InlineData("""{"messageId":"7d0f7d0f-0000-0000-0000-000000000001","type":"a","version":0,"occurredAtUtc":"2038-01-01T00:00:00Z","payload":{}}""", "Version")]
    [InlineData("""{"messageId":"7d0f7d0f-0000-0000-0000-000000000001","type":"a","version":1,"occurredAtUtc":"2038-01-01T00:00:00Z","payload":[]}""", "Payload")]
    public void An_invalid_envelope_is_refused_with_the_reason(string body, string reason)
    {
        Assert.Null(IntegrationEventEnvelope.TryParse(Encoding.UTF8.GetBytes(body), out var error));
        Assert.Contains(reason, error);
    }
}

/// <summary>Operational events → integration events: an explicit table, and only for the published events.</summary>
public sealed class IntegrationEventMapperTests
{
    private static readonly DateTimeOffset At = new(2038, 3, 1, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Published_operational_events_map_to_their_integration_event_with_the_correlation_id()
    {
        var correlationId = Guid.NewGuid();
        var assignment = new AssignmentCreatedPayload(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], 15, 30, At, At.AddHours(2));

        var created = Assert.IsType<AssignmentCreatedIntegrationEvent>(IntegrationEventMapper.Map(AssignmentEvents.AssignmentCreated, At, assignment, correlationId));

        Assert.Equal((assignment.AssignmentId, assignment.VisitId, assignment.TechnicianId, assignment.VehicleId, At, (Guid?)correlationId),
            (created.AssignmentId, created.VisitId, created.TechnicianId, created.VehicleId, created.OccurredAtUtc, created.CorrelationId));
        Assert.Equal(assignment.EquipmentIds, created.EquipmentIds);
        Assert.NotEqual(Guid.Empty, created.EventId);

        var completed = Assert.IsType<VisitWorkCompletedIntegrationEvent>(IntegrationEventMapper.Map(
            VisitExecutionEvents.VisitWorkCompleted, At, new VisitWorkCompletedPayload(Guid.NewGuid(), Guid.NewGuid(), At, null, 120m, 15m, 105m, null), null));
        Assert.Equal((null, 120m, 15m, 105m), (completed.TravelMinutes, completed.GrossWorkMinutes, completed.PauseMinutes, completed.NetWorkMinutes));

        Assert.IsType<AssignmentReplacedIntegrationEvent>(IntegrationEventMapper.Map(AssignmentEvents.AssignmentReplaced, At, new AssignmentReplacedPayload(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), null));
        Assert.IsType<AssignmentCancelledIntegrationEvent>(IntegrationEventMapper.Map(AssignmentEvents.AssignmentCancelled, At, new AssignmentCancelledPayload(Guid.NewGuid(), Guid.NewGuid()), null));
        Assert.IsType<IncidentDispatchedIntegrationEvent>(IntegrationEventMapper.Map(
            IncidentEvents.IncidentDispatched, At, new IncidentDispatchedPayload(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, []), null));
    }

    [Fact]
    public void Visit_creation_and_status_changes_are_published_with_the_context_read_sides_need()
    {
        var payload = new VisitCreatedPayload(Guid.NewGuid(), Guid.NewGuid(), At.AddDays(1), At.AddDays(1).AddHours(1), Guid.NewGuid(), Guid.NewGuid(), WorkOrderPriority.Urgent);

        var created = Assert.IsType<VisitCreatedIntegrationEvent>(IntegrationEventMapper.Map(WorkOrderEvents.VisitCreated, At, payload, null));

        Assert.Equal(
            (payload.VisitId, payload.WorkOrderId, payload.CustomerId, payload.SiteId, payload.Start, payload.End, At, "Urgent"),
            (created.VisitId, created.WorkOrderId, created.CustomerId, created.SiteId, created.PlannedStartUtc, created.PlannedEndUtc, created.CreatedAtUtc, created.WorkOrderPriority));

        var changed = Assert.IsType<VisitStatusChangedIntegrationEvent>(IntegrationEventMapper.Map(
            WorkOrderEvents.VisitStatusChanged, At, new VisitStatusChangedPayload(payload.VisitId, VisitStatus.InProgress, VisitStatus.Completed), null));
        Assert.Equal((payload.VisitId, "InProgress", "Completed"), (changed.VisitId, changed.OldStatus, changed.NewStatus));
    }

    [Fact]
    public void A_rescheduled_visit_is_published_with_old_and_new_window_reason_and_incident()
    {
        var incidentId = Guid.NewGuid();
        var payload = new VisitRescheduledPayload(Guid.NewGuid(), At, At.AddHours(1), At.AddHours(4), At.AddHours(5), VisitRescheduleReasons.UrgentIncident, incidentId);
        var correlationId = Guid.NewGuid();

        var rescheduled = Assert.IsType<VisitRescheduledIntegrationEvent>(IntegrationEventMapper.Map(WorkOrderEvents.VisitRescheduled, At, payload, correlationId));

        Assert.Equal(
            (payload.VisitId, At, At.AddHours(1), At.AddHours(4), At.AddHours(5), "UrgentIncident", (Guid?)incidentId, (Guid?)correlationId),
            (rescheduled.VisitId, rescheduled.OldStartUtc, rescheduled.OldEndUtc, rescheduled.NewStartUtc, rescheduled.NewEndUtc, rescheduled.Reason, rescheduled.IncidentId, rescheduled.CorrelationId));

        // The incident's own summary event stays internal history.
        Assert.Null(IntegrationEventMapper.Map(IncidentEvents.RescheduleApplied, At, new RescheduleAppliedPayload(incidentId, Guid.NewGuid(), payload.VisitId, Guid.NewGuid(), 210), null));
    }

    [Fact]
    public void Internal_operational_events_are_not_published()
    {
        Assert.Null(IntegrationEventMapper.Map(WorkOrderEvents.WorkOrderCreated, At, new WorkOrderCreatedPayload(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), WorkOrderPriority.Normal), null));
        Assert.Null(IntegrationEventMapper.Map(VisitExecutionEvents.VisitWorkStarted, At, new VisitWorkStartedPayload(Guid.NewGuid(), Guid.NewGuid(), At, null), null));
        Assert.Null(IntegrationEventMapper.Map(IncidentEvents.IncidentCreated, At, new object(), null));

        // A known type with a payload of the wrong shape is not guessed at.
        Assert.Null(IntegrationEventMapper.Map(AssignmentEvents.AssignmentCreated, At, new AssignmentCancelledPayload(Guid.NewGuid(), Guid.NewGuid()), null));
    }
}
