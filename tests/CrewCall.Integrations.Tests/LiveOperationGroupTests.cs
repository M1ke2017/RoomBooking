using System.Reflection;
using CrewCall.Contracts.Integration;
using CrewCall.Integrations.Live;
using Xunit;
using static CrewCall.Integrations.Tests.LiveTestKit;

namespace CrewCall.Integrations.Tests;

/// <summary>Group names are built on the server from typed ids, and every message is routed to all + its related groups.</summary>
public sealed class LiveOperationGroupTests
{
    [Fact]
    public void The_all_group_is_all() => Assert.Equal("all", LiveOperationGroups.All);

    [Fact]
    public void Site_technician_and_incident_groups_are_deterministic_lower_case_names_of_the_id()
    {
        var id = Guid.Parse("8C1D2E3F-4A5B-4C6D-8E7F-9A0B1C2D3E4F");

        Assert.Equal("site:8c1d2e3f-4a5b-4c6d-8e7f-9a0b1c2d3e4f", LiveOperationGroups.Site(id));
        Assert.Equal("technician:8c1d2e3f-4a5b-4c6d-8e7f-9a0b1c2d3e4f", LiveOperationGroups.Technician(id));
        Assert.Equal("incident:8c1d2e3f-4a5b-4c6d-8e7f-9a0b1c2d3e4f", LiveOperationGroups.Incident(id));
        Assert.Equal(LiveOperationGroups.Site(id), LiveOperationGroups.Site(Guid.Parse(id.ToString().ToUpperInvariant())));
        Assert.NotEqual(LiveOperationGroups.Site(id), LiveOperationGroups.Technician(id));
    }

    [Fact]
    public void The_empty_id_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => LiveOperationGroups.Site(Guid.Empty));
        Assert.Throws<ArgumentException>(() => LiveOperationGroups.Technician(Guid.Empty));
        Assert.Throws<ArgumentException>(() => LiveOperationGroups.Incident(Guid.Empty));
    }

    [Fact]
    public void No_hub_method_accepts_a_group_name_or_any_string_from_the_client()
    {
        var methods = typeof(LiveOperationsHub).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName && method.Name is not ("OnConnectedAsync" or "OnDisconnectedAsync"))
            .ToList();

        Assert.Equal(
            ["SubscribeAll", "SubscribeIncident", "SubscribeSite", "SubscribeTechnician",
             "UnsubscribeAll", "UnsubscribeIncident", "UnsubscribeSite", "UnsubscribeTechnician"],
            methods.Select(method => method.Name).Order());
        Assert.All(methods.SelectMany(method => method.GetParameters()), parameter => Assert.Equal(typeof(Guid), parameter.ParameterType));
    }

    [Fact]
    public void Incident_dispatched_goes_to_all_the_incident_the_technician_and_the_site()
    {
        var dispatched = new IncidentDispatchedIntegrationEvent(
            Guid.NewGuid(), OccurredAt, null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var siteId = Guid.NewGuid();
        var envelope = Envelope(dispatched);

        var groups = LiveOperationGroups.For(LiveOperationMapper.Map(envelope, dispatched, new LiveRoutingContext(siteId, [])));

        Assert.Equal(
            ["all", LiveOperationGroups.Incident(dispatched.IncidentId), LiveOperationGroups.Technician(dispatched.TechnicianId),
             LiveOperationGroups.Site(siteId)],
            groups);
    }

    [Fact]
    public void Assignment_created_goes_to_all_the_technician_and_the_site()
    {
        var created = new AssignmentCreatedIntegrationEvent(Guid.NewGuid(), OccurredAt, null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, []);
        var siteId = Guid.NewGuid();

        var groups = LiveOperationGroups.For(LiveOperationMapper.Map(Envelope(created), created, new LiveRoutingContext(siteId, [])));

        Assert.Equal(["all", LiveOperationGroups.Technician(created.TechnicianId), LiveOperationGroups.Site(siteId)], groups);
    }

    [Fact]
    public void Visit_work_completed_goes_to_all_the_looked_up_technician_and_the_site()
    {
        var completed = new VisitWorkCompletedIntegrationEvent(Guid.NewGuid(), OccurredAt, null, Guid.NewGuid(), Guid.NewGuid(), null, 30m, 0m, 30m);
        var (siteId, technicianId) = (Guid.NewGuid(), Guid.NewGuid());

        var groups = LiveOperationGroups.For(LiveOperationMapper.Map(Envelope(completed), completed, new LiveRoutingContext(siteId, [technicianId])));

        Assert.Equal(["all", LiveOperationGroups.Technician(technicianId), LiveOperationGroups.Site(siteId)], groups);
    }

    [Fact]
    public void Assignment_replaced_goes_to_both_technicians_once_each()
    {
        var replaced = new AssignmentReplacedIntegrationEvent(Guid.NewGuid(), OccurredAt, null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (oldTechnician, newTechnician) = (Guid.NewGuid(), Guid.NewGuid());

        var groups = LiveOperationGroups.For(LiveOperationMapper.Map(
            Envelope(replaced), replaced, new LiveRoutingContext(null, [oldTechnician, newTechnician, oldTechnician])));

        Assert.Equal(["all", LiveOperationGroups.Technician(oldTechnician), LiveOperationGroups.Technician(newTechnician)], groups);
    }
}
