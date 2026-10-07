using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CrewCall.Contracts.Absences;
using CrewCall.Contracts.Incidents;
using CrewCall.Contracts.Scheduling;
using CrewCall.Contracts.Visits;
using CrewCall.Contracts.WorkOrders;
using CrewCall.Scheduling.Reservations;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using static CrewCall.Api.Tests.IncidentTestKit;

namespace CrewCall.Api.Tests;

/// <summary>The urgent incident workflow end to end: create, analyze, prepare, dispatch, resolve, cancel.</summary>
public sealed class IncidentEndpointsTests(CrewCallApiFactory factory)
{
    // ---- Incident domain -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_returns_201_with_a_new_incident_normalized_skills_and_an_IncidentCreated_event()
    {
        using var client = factory.CreateClient();
        var customerId = await ApiTestData.CreateCustomerAsync(client);
        var siteId = await ApiTestData.CreateSiteAsync(client, customerId);
        var start = new DateTimeOffset(2034, 6, 1, 12, 0, 0, TimeSpan.FromHours(2));

        using var response = await client.PostAsJsonAsync("/api/incidents",
            IncidentRequest(customerId, siteId, start, start.AddHours(2), "critical", " fiber ", "hvac"), Cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var incident = (await response.Content.ReadFromJsonAsync<IncidentResponse>(Cancellation))!;
        Assert.Equal($"/api/incidents/{incident.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(("New", "Critical", customerId, siteId), (incident.Status, incident.Priority, incident.CustomerId, incident.SiteId));
        Assert.Equal(["FIBER", "HVAC"], incident.RequiredSkillCodes);
        Assert.Equal((start.ToUniversalTime(), start.AddHours(2).ToUniversalTime()), (incident.RequestedStart, incident.RequestedEnd));
        Assert.Null(incident.WorkOrderId);
        Assert.Null(incident.ResolvedAtUtc);
        AssertSameJson(incident, await GetAsync<IncidentResponse>(client, $"/api/incidents/{incident.Id}"));

        var created = Assert.Single(await EventsAsync(client, "incident", incident.Id));
        Assert.Equal("IncidentCreated", created.EventType);
        Assert.Equal("Critical", created.Payload.GetProperty("priority").GetString());
        Assert.Equal(["FIBER", "HVAC"], created.Payload.GetProperty("requiredSkillCodes").EnumerateArray().Select(code => code.GetString()));
    }

    [Fact]
    public async Task Create_returns_404_for_a_missing_customer_or_site()
    {
        using var client = factory.CreateClient();
        var customerId = await ApiTestData.CreateCustomerAsync(client);
        var siteId = await ApiTestData.CreateSiteAsync(client, customerId);
        var day = NewDay();

        using var missingCustomer = await client.PostAsJsonAsync("/api/incidents",
            IncidentRequest(Guid.NewGuid(), siteId, day, day.AddHours(1)), Cancellation);
        using var missingSite = await client.PostAsJsonAsync("/api/incidents",
            IncidentRequest(customerId, Guid.NewGuid(), day, day.AddHours(1)), Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, missingCustomer.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingSite.StatusCode);
    }

    [Fact]
    public async Task Create_returns_400_for_a_site_of_another_customer()
    {
        using var client = factory.CreateClient();
        var customerId = await ApiTestData.CreateCustomerAsync(client);
        var otherSiteId = await ApiTestData.CreateSiteAsync(client, await ApiTestData.CreateCustomerAsync(client));
        var day = NewDay();

        using var response = await client.PostAsJsonAsync("/api/incidents",
            IncidentRequest(customerId, otherSiteId, day, day.AddHours(1)), Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Cancellation);
        Assert.Contains("siteId", problem!.Errors.Keys);
    }

    [Theory]
    [InlineData(0, "requestedEnd")]
    [InlineData(-60, "requestedEnd")]
    [InlineData(32 * 24 * 60, "requestedEnd")]
    public async Task Create_returns_400_for_an_invalid_requested_window(int minutes, string field)
    {
        using var client = factory.CreateClient();
        var customerId = await ApiTestData.CreateCustomerAsync(client);
        var siteId = await ApiTestData.CreateSiteAsync(client, customerId);
        var day = NewDay();

        using var response = await client.PostAsJsonAsync("/api/incidents",
            IncidentRequest(customerId, siteId, day, day.AddMinutes(minutes)), Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Cancellation))!.Errors.Keys);
    }

    [Theory]
    [InlineData("priority", "Low", "FIBER")]
    [InlineData("requiredSkillCodes", "High", "FIBER", " fiber ")]
    [InlineData("requiredSkillCodes", "High", "FIBER", " ")]
    public async Task Create_returns_400_for_an_invalid_priority_or_skill_list(string field, string priority, params string[] skills)
    {
        using var client = factory.CreateClient();
        var customerId = await ApiTestData.CreateCustomerAsync(client);
        var siteId = await ApiTestData.CreateSiteAsync(client, customerId);
        var day = NewDay();

        using var response = await client.PostAsJsonAsync("/api/incidents",
            IncidentRequest(customerId, siteId, day, day.AddHours(1), priority, skills), Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Cancellation))!.Errors.Keys);
    }

    [Fact]
    public async Task Get_returns_404_for_a_missing_incident()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/incidents/{Guid.NewGuid()}", Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_filters_by_status_and_priority_newest_first()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var high = await CreateIncidentAsync(client, day.AddHours(1), day.AddHours(2), "High");
        var critical = await CreateIncidentAsync(client, day.AddHours(3), day.AddHours(4), "Critical");
        var ready = await ReadyIncidentAsync(client, day, [technician], "Critical");

        var criticalOnes = await GetAsync<IncidentResponse[]>(client, "/api/incidents?priority=critical");
        var readyOnes = await GetAsync<IncidentResponse[]>(client, "/api/incidents?status=ReadyForDispatch&priority=Critical");
        var all = await GetAsync<IncidentResponse[]>(client, "/api/incidents");

        Assert.Equal([ready.Id, critical.Id], criticalOnes.Select(i => i.Id).Where(id => id == ready.Id || id == critical.Id));
        Assert.DoesNotContain(criticalOnes, i => i.Priority != "Critical");
        Assert.Contains(readyOnes, i => i.Id == ready.Id);
        Assert.DoesNotContain(readyOnes, i => i.Status != "ReadyForDispatch" || i.Priority != "Critical");
        var mine = all.Where(i => i.Id == high.Id || i.Id == critical.Id || i.Id == ready.Id).Select(i => i.Id);
        Assert.Equal([ready.Id, critical.Id, high.Id], mine);
        Assert.Equal(
            all.OrderByDescending(i => i.CreatedAtUtc).ThenBy(i => i.Id).Select(i => i.Id),
            all.Select(i => i.Id));

        using var invalid = await client.GetAsync("/api/incidents?status=Open", Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Lifecycle_is_enforced_with_409_for_invalid_transitions()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var incident = await CreateIncidentAsync(client, day.AddHours(10), day.AddHours(12));

        // New: cannot be resolved or dispatched (not analysed).
        using (var resolveNew = await client.PostAsync($"/api/incidents/{incident.Id}/resolve", null, Cancellation))
        {
            Assert.Equal(HttpStatusCode.Conflict, resolveNew.StatusCode);
        }

        using (var dispatchNew = await DispatchAsync(client, incident.Id, Dispatch(technician)))
        {
            Assert.Equal(HttpStatusCode.Conflict, dispatchNew.StatusCode);
        }

        // New → Analyzing → ReadyForDispatch → Cancelled; then nothing more.
        var analysis = await AnalyzeAsync(client, incident.Id, technician);
        Assert.Equal("ReadyForDispatch", analysis.IncidentStatus);
        var cancelled = await PostAsync<IncidentResponse>(client, $"/api/incidents/{incident.Id}/cancel", null);
        Assert.Equal("Cancelled", cancelled.Status);

        foreach (var action in new[] { "analyze", "cancel", "resolve" })
        {
            using var response = await client.PostAsJsonAsync($"/api/incidents/{incident.Id}/{action}", new { }, Cancellation);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        using (var dispatchCancelled = await DispatchAsync(client, incident.Id, Dispatch(technician)))
        {
            Assert.Equal(HttpStatusCode.Conflict, dispatchCancelled.StatusCode);
        }

        Assert.Equal(["IncidentCreated", "IncidentAnalyzed", "IncidentCancelled"], await IncidentEventTypesAsync(factory, incident.Id));

        using var missing = await client.PostAsync($"/api/incidents/{Guid.NewGuid()}/cancel", null, Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // ---- Analysis ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Analyze_suggests_direct_assignment_reschedule_and_unavailable_with_the_impact_on_existing_work()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var code = UniqueCode("FIB");
        var skill = await CreateSkillAsync(client, code);
        var free = await CreateTechnicianAsync(client, true, skill);
        var busy = await CreateTechnicianAsync(client, true, skill);
        var unskilled = await CreateTechnicianAsync(client);
        var absent = await CreateTechnicianAsync(client, true, skill);
        var inactive = await CreateTechnicianAsync(client, false, skill);
        var existing = await ExistingWorkAsync(client, busy, day.AddHours(11), day.AddHours(13));
        await PostAsync<AbsenceResponse>(client, $"/api/technicians/{absent}/absences",
            new CreateAbsenceRequest(day.AddHours(9), day.AddHours(13), "SickLeave", null));
        var incident = await CreateIncidentAsync(client, day.AddHours(10), day.AddHours(12), "Critical", code.ToLowerInvariant());

        using var response = await client.PostAsJsonAsync($"/api/incidents/{incident.Id}/analyze",
            new AnalyzeIncidentRequest([inactive, absent, unskilled, busy, free], null), Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var analysis = (await response.Content.ReadFromJsonAsync<IncidentAnalysisResponse>(Cancellation))!;
        Assert.Equal((incident.Id, "ReadyForDispatch", 5, 1, 4), (analysis.IncidentId, analysis.IncidentStatus, analysis.CandidatesEvaluated, analysis.EligibleCount, analysis.RejectedCount));
        Assert.Equal([1, 2, 3, 4, 5], analysis.Suggestions.Select(s => s.Rank));
        var byTechnician = analysis.Suggestions.ToDictionary(s => s.TechnicianId);

        var direct = analysis.Suggestions[0];
        Assert.Equal((free, "DirectAssignment", 100m, true), (direct.TechnicianId, direct.SuggestionType, direct.Score, direct.SchedulingChecked));
        Assert.Empty(direct.Reasons);
        Assert.Empty(direct.Impact);

        var reschedule = analysis.Suggestions[1];
        Assert.Equal((busy, "RequiresReschedule"), (reschedule.TechnicianId, reschedule.SuggestionType));
        Assert.NotNull(reschedule.Score);
        Assert.Equal("TechnicianReservationConflict", Assert.Single(reschedule.Reasons).Code);
        Assert.Equal((existing.Visit.Id, existing.WorkOrder.Id), (reschedule.ConflictingVisitId, reschedule.ConflictingWorkOrderId));
        Assert.Equal((existing.Assignment.ClaimedStart, existing.Assignment.ClaimedEnd), (reschedule.ConflictStart, reschedule.ConflictEnd));
        var impact = Assert.Single(reschedule.Impact);
        Assert.Equal(
            ("Technician", busy, existing.Visit.Id, existing.WorkOrder.Id, false),
            (impact.ResourceType, impact.ResourceId, impact.ConflictingVisitId, impact.ConflictingWorkOrderId, impact.WithinTravelBuffer));

        Assert.All([unskilled, absent, inactive], id =>
        {
            Assert.Equal("Unavailable", byTechnician[id].SuggestionType);
            Assert.Null(byTechnician[id].Score);
            Assert.Empty(byTechnician[id].Impact);
        });
        Assert.Equal("MissingRequiredSkills", Assert.Single(byTechnician[unskilled].Reasons).Code);
        Assert.Equal("TechnicianUnavailable", Assert.Single(byTechnician[absent].Reasons).Code);
        Assert.Contains(byTechnician[inactive].Reasons, reason => reason.Code == "TechnicianInactive");
        Assert.Equal(new[] { unskilled, absent, inactive }.Order(), analysis.Suggestions.Skip(2).Select(s => s.TechnicianId));

        // The existing work is untouched: still assigned to the busy technician, same visit, same window.
        var stillActive = await GetAsync<AssignmentResponse>(client, $"/api/visits/{existing.Visit.Id}/assignment");
        AssertSameJson(existing.Assignment, stillActive);
    }

    [Fact]
    public async Task Analyze_keeps_the_matching_rank_order_and_returns_several_suggestions()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var idle = await CreateTechnicianAsync(client);
        var loaded = await CreateTechnicianAsync(client);
        var lightlyLoaded = await CreateTechnicianAsync(client);
        await ExistingWorkAsync(client, loaded, day.AddHours(5), day.AddHours(9));
        await ExistingWorkAsync(client, lightlyLoaded, day.AddHours(6), day.AddHours(7));
        var incident = await CreateIncidentAsync(client, day.AddHours(10), day.AddHours(12));

        var analysis = await AnalyzeAsync(client, incident.Id, loaded, idle, lightlyLoaded);
        using var matchResponse = await client.PostAsJsonAsync("/api/scheduling/match",
            new ResourceMatchingRequest(day.AddHours(10), day.AddHours(12), null, null, [loaded, idle, lightlyLoaded], null, null, null, null, null, null),
            Cancellation);
        var match = (await matchResponse.Content.ReadFromJsonAsync<ResourceMatchingResponse>(Cancellation))!;

        Assert.Equal([idle, lightlyLoaded, loaded], analysis.Suggestions.Select(s => s.TechnicianId));
        Assert.Equal(match.EligibleCandidates.Select(c => c.TechnicianId), analysis.Suggestions.Select(s => s.TechnicianId));
        Assert.Equal(match.EligibleCandidates.Select(c => (decimal?)c.TotalScore), analysis.Suggestions.Select(s => s.Score));
        Assert.All(analysis.Suggestions, s => Assert.Equal(("DirectAssignment", true), (s.SuggestionType, s.SchedulingChecked)));
    }

    [Fact]
    public async Task Analyze_claims_nothing_and_records_only_the_analysis()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var first = await CreateTechnicianAsync(client);
        var second = await CreateTechnicianAsync(client);
        var incident = await CreateIncidentAsync(client, day.AddHours(10), day.AddHours(12));

        var analysis = await AnalyzeAsync(client, incident.Id, first, second);
        var again = await AnalyzeAsync(client, incident.Id, first, second);

        Assert.Equal(2, analysis.Suggestions.Count(s => s.SuggestionType == "DirectAssignment"));
        Assert.Equal("ReadyForDispatch", again.IncidentStatus); // re-analysis is allowed and keeps the status
        Assert.Equal(new DatabaseState(0, 0, 0, 0, 0), await StateAsync(factory, incident.CustomerId, first, second));
        Assert.Equal(["IncidentCreated", "IncidentAnalyzed", "IncidentAnalyzed"], await IncidentEventTypesAsync(factory, incident.Id));
        var current = await GetAsync<IncidentResponse>(client, $"/api/incidents/{incident.Id}");
        Assert.Equal(("ReadyForDispatch", (Guid?)null), (current.Status, current.WorkOrderId));
    }

    [Fact]
    public async Task Analyze_returns_404_for_a_missing_incident_or_candidate_and_works_without_a_body()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var incident = await CreateIncidentAsync(client, day.AddHours(10), day.AddHours(12));

        using var missingIncident = await client.PostAsJsonAsync($"/api/incidents/{Guid.NewGuid()}/analyze", new AnalyzeIncidentRequest(null, null), Cancellation);
        using var missingCandidate = await client.PostAsJsonAsync($"/api/incidents/{incident.Id}/analyze",
            new AnalyzeIncidentRequest([Guid.NewGuid()], null), Cancellation);
        using var invalid = await client.PostAsJsonAsync($"/api/incidents/{incident.Id}/analyze", new AnalyzeIncidentRequest([], null), Cancellation);
        using var withoutBody = await client.PostAsync($"/api/incidents/{incident.Id}/analyze", null, Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, missingIncident.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingCandidate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withoutBody.StatusCode);
    }

    // ---- Prepare dispatch ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Prepare_returns_CanDispatch_true_for_a_free_selection_and_changes_nothing()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var incident = await ReadyIncidentAsync(client, day, [technician]);

        var prepared = await PostAsync<PrepareIncidentDispatchResponse>(client, $"/api/incidents/{incident.Id}/prepare-dispatch",
            new PrepareIncidentDispatchRequest(technician, null, null, 30, 15));

        Assert.True(prepared.CanDispatch);
        Assert.Empty(prepared.Reasons);
        Assert.Empty(prepared.Impact);
        Assert.Equal((day.AddHours(9.5), day.AddHours(12.25)), (prepared.EffectiveStart, prepared.EffectiveEnd));
        Assert.Equal(new DatabaseState(0, 0, 0, 0, 0), await StateAsync(factory, incident.CustomerId, technician));
        Assert.Equal(["IncidentCreated", "IncidentAnalyzed"], await IncidentEventTypesAsync(factory, incident.Id));
        Assert.Equal("ReadyForDispatch", (await GetAsync<IncidentResponse>(client, $"/api/incidents/{incident.Id}")).Status);
    }

    [Fact]
    public async Task Prepare_returns_200_with_CanDispatch_false_reasons_and_impact_for_a_conflict()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var vehicle = await CreateVehicleAsync(client);
        var existing = await ExistingWorkAsync(client, await CreateTechnicianAsync(client), day.AddHours(11), day.AddHours(13), vehicle.Id);
        var travel = await ExistingWorkAsync(client, technician, day.AddHours(7), day.AddHours(9).AddMinutes(45));
        var incident = await ReadyIncidentAsync(client, day, [technician]);

        using var response = await client.PostAsJsonAsync($"/api/incidents/{incident.Id}/prepare-dispatch",
            new PrepareIncidentDispatchRequest(technician, vehicle.Id, null, 30, null), Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var prepared = (await response.Content.ReadFromJsonAsync<PrepareIncidentDispatchResponse>(Cancellation))!;
        Assert.False(prepared.CanDispatch);
        Assert.Equal(["TravelBufferConflict", "VehicleReservationConflict"], prepared.Reasons.Select(r => r.Code).Order());
        Assert.Equal(
            [(travel.Visit.Id, travel.WorkOrder.Id, "Technician", true), (existing.Visit.Id, existing.WorkOrder.Id, "Vehicle", false)],
            prepared.Impact.Select(i => (i.ConflictingVisitId!.Value, i.ConflictingWorkOrderId!.Value, i.ResourceType, i.WithinTravelBuffer)));
        Assert.Equal(new DatabaseState(0, 0, 1, 1, 0), await StateAsync(factory, incident.CustomerId, technician));
    }

    [Fact]
    public async Task Prepare_returns_404_for_missing_resources_and_409_before_analysis()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var incident = await ReadyIncidentAsync(client, day, [technician]);
        var fresh = await CreateIncidentAsync(client, day.AddHours(14), day.AddHours(15));

        async Task<HttpStatusCode> PrepareAsync(Guid incidentId, PrepareIncidentDispatchRequest request)
        {
            using var response = await client.PostAsJsonAsync($"/api/incidents/{incidentId}/prepare-dispatch", request, Cancellation);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.NotFound, await PrepareAsync(incident.Id, new(Guid.NewGuid(), null, null, null, null)));
        Assert.Equal(HttpStatusCode.NotFound, await PrepareAsync(incident.Id, new(technician, Guid.NewGuid(), null, null, null)));
        Assert.Equal(HttpStatusCode.NotFound, await PrepareAsync(incident.Id, new(technician, null, [Guid.NewGuid()], null, null)));
        Assert.Equal(HttpStatusCode.NotFound, await PrepareAsync(Guid.NewGuid(), new(technician, null, null, null, null)));
        Assert.Equal(HttpStatusCode.BadRequest, await PrepareAsync(incident.Id, new(null, null, null, null, null)));
        Assert.Equal(HttpStatusCode.BadRequest, await PrepareAsync(incident.Id, new(technician, null, null, 500, null)));
        Assert.Equal(HttpStatusCode.Conflict, await PrepareAsync(fresh.Id, new(technician, null, null, null, null)));
    }

    // ---- Dispatch --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dispatch_creates_the_work_order_visit_assignment_and_reservations_atomically_with_every_event()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var code = UniqueCode("HV");
        var technician = await CreateTechnicianAsync(client, true, await CreateSkillAsync(client, code));
        var vehicle = await CreateVehicleAsync(client);
        var equipment = await CreateEquipmentAsync(client);
        var incident = await ReadyIncidentAsync(client, day, [technician], "Critical", code);

        using var response = await DispatchAsync(client, incident.Id,
            new DispatchIncidentRequest(technician, vehicle.Id, [equipment.Id], 15, 30));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dispatched = (await response.Content.ReadFromJsonAsync<IncidentDispatchResponse>(Cancellation))!;
        Assert.Equal($"/api/work-orders/{dispatched.WorkOrderId}", response.Headers.Location?.OriginalString);

        // Incident: Dispatched and linked.
        Assert.Equal(("Dispatched", (Guid?)dispatched.WorkOrderId), (dispatched.Incident.Status, dispatched.Incident.WorkOrderId));
        AssertSameJson(dispatched.Incident, await GetAsync<IncidentResponse>(client, $"/api/incidents/{incident.Id}"));

        // Work order from the incident; Critical becomes Urgent.
        var workOrder = await GetAsync<WorkOrderResponse>(client, $"/api/work-orders/{dispatched.WorkOrderId}");
        Assert.Equal(
            (incident.CustomerId, incident.SiteId, incident.Title, incident.Description, "Urgent", "Open"),
            (workOrder.CustomerId, workOrder.SiteId, workOrder.Title, workOrder.Description, workOrder.Priority, workOrder.Status));

        // Visit over the requested window, Planned.
        var visit = Assert.Single(await GetAsync<VisitResponse[]>(client, $"/api/work-orders/{workOrder.Id}/visits"));
        Assert.Equal((dispatched.VisitId, incident.RequestedStart, incident.RequestedEnd, "Planned"), (visit.Id, visit.Start, visit.End, visit.Status));

        // Assignment: the visit's active assignment, with every resource claimed over the buffered window.
        var assignment = await GetAsync<AssignmentResponse>(client, $"/api/visits/{visit.Id}/assignment");
        AssertSameJson(dispatched.Assignment, assignment);
        Assert.Equal((technician, (Guid?)vehicle.Id, equipment.Id, "Active"), (assignment.TechnicianId, assignment.VehicleId, Assert.Single(assignment.EquipmentIds), assignment.Status));
        Assert.Equal((day.AddHours(9.75), day.AddHours(12.5)), (assignment.ClaimedStart, assignment.ClaimedEnd));
        Assert.Equal(1, await ReservationCountAsync(factory, technician, ResourceType.Technician));
        Assert.Equal(1, await ReservationCountAsync(factory, vehicle.Id, ResourceType.Vehicle));
        Assert.Equal(1, await ReservationCountAsync(factory, equipment.Id, ResourceType.Equipment));

        // The resources are really taken: the same technician cannot be dispatched to an overlapping incident.
        var other = await ReadyIncidentAsync(client, day, [technician]);
        using var overlapping = await DispatchAsync(client, other.Id, Dispatch(technician));
        Assert.Equal(HttpStatusCode.Conflict, overlapping.StatusCode);

        // Events: the incident's own, and the regular events of the work order, visit and assignment.
        Assert.Equal(["IncidentCreated", "IncidentAnalyzed", "IncidentDispatched"], await IncidentEventTypesAsync(factory, incident.Id));
        var incidentDispatched = (await EventsAsync(client, "incident", incident.Id)).Single(e => e.EventType == "IncidentDispatched").Payload;
        Assert.Equal(
            (incident.Id, workOrder.Id, visit.Id, assignment.AssignmentId, technician, vehicle.Id, equipment.Id),
            (incidentDispatched.GetProperty("incidentId").GetGuid(), incidentDispatched.GetProperty("workOrderId").GetGuid(),
             incidentDispatched.GetProperty("visitId").GetGuid(), incidentDispatched.GetProperty("assignmentId").GetGuid(),
             incidentDispatched.GetProperty("technicianId").GetGuid(), incidentDispatched.GetProperty("vehicleId").GetGuid(),
             incidentDispatched.GetProperty("equipmentIds").EnumerateArray().Single().GetGuid()));
        Assert.Equal(["WorkOrderCreated"], (await EventsAsync(client, "work-order", workOrder.Id)).Select(e => e.EventType));
        Assert.Equal(["VisitCreated"], (await EventsAsync(client, "visit", visit.Id)).Select(e => e.EventType));
        Assert.Equal(["AssignmentCreated"], (await EventsAsync(client, "assignment", assignment.AssignmentId)).Select(e => e.EventType));
    }

    [Theory]
    [InlineData("High", "High")]
    [InlineData("Urgent", "Urgent")]
    [InlineData("Critical", "Urgent")]
    public async Task Dispatch_maps_the_incident_priority_to_the_work_order_priority(string incidentPriority, string workOrderPriority)
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var incident = await ReadyIncidentAsync(client, day, [technician], incidentPriority);

        using var response = await DispatchAsync(client, incident.Id, Dispatch(technician));
        var dispatched = (await response.Content.ReadFromJsonAsync<IncidentDispatchResponse>(Cancellation))!;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(incidentPriority, dispatched.Incident.Priority);
        Assert.Equal(workOrderPriority, (await GetAsync<WorkOrderResponse>(client, $"/api/work-orders/{dispatched.WorkOrderId}")).Priority);
    }

    [Fact]
    public async Task Dispatch_runs_the_final_check_and_writes_nothing_when_it_fails()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var code = UniqueCode("GAS");
        var unskilled = await CreateTechnicianAsync(client);
        var busy = await CreateTechnicianAsync(client, true, await CreateSkillAsync(client, code));
        var existing = await ExistingWorkAsync(client, busy, day.AddHours(11), day.AddHours(12));
        var incident = await ReadyIncidentAsync(client, day, [unskilled, busy], "Urgent", code);

        using var unskilledResponse = await DispatchAsync(client, incident.Id, Dispatch(unskilled));
        using var busyResponse = await DispatchAsync(client, incident.Id, Dispatch(busy));

        Assert.Equal(HttpStatusCode.Conflict, unskilledResponse.StatusCode);
        var skills = await unskilledResponse.Content.ReadFromJsonAsync<ProblemDetails>(Cancellation);
        Assert.Equal("MissingRequiredSkills", ((JsonElement)skills!.Extensions["reasons"]!)[0].GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.Conflict, busyResponse.StatusCode);
        var busyProblem = await busyResponse.Content.ReadFromJsonAsync<ProblemDetails>(Cancellation);
        var impact = ((JsonElement)busyProblem!.Extensions["impact"]!)[0];
        Assert.Equal((existing.Visit.Id, existing.WorkOrder.Id),
            (impact.GetProperty("conflictingVisitId").GetGuid(), impact.GetProperty("conflictingWorkOrderId").GetGuid()));

        Assert.Equal(new DatabaseState(0, 0, 1, 1, 0), await StateAsync(factory, incident.CustomerId, unskilled, busy));
        Assert.Equal("ReadyForDispatch", (await GetAsync<IncidentResponse>(client, $"/api/incidents/{incident.Id}")).Status);
        Assert.Equal(["IncidentCreated", "IncidentAnalyzed"], await IncidentEventTypesAsync(factory, incident.Id));
        AssertSameJson(existing.Assignment, await GetAsync<AssignmentResponse>(client, $"/api/visits/{existing.Visit.Id}/assignment"));
    }

    [Fact]
    public async Task A_second_dispatch_returns_409_and_creates_no_second_work_order()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var other = await CreateTechnicianAsync(client);
        var incident = await ReadyIncidentAsync(client, day, [technician, other]);

        using var first = await DispatchAsync(client, incident.Id, Dispatch(technician));
        using var second = await DispatchAsync(client, incident.Id, Dispatch(other));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("Incident already dispatched", (await second.Content.ReadFromJsonAsync<ProblemDetails>(Cancellation))!.Title);
        Assert.Equal(new DatabaseState(1, 1, 1, 1, 1), await StateAsync(factory, incident.CustomerId, technician, other));
    }

    [Fact]
    public async Task Dispatch_returns_400_404_for_invalid_or_missing_input()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var equipment = await CreateEquipmentAsync(client);
        var incident = await ReadyIncidentAsync(client, day, [technician]);

        using var duplicateEquipment = await DispatchAsync(client, incident.Id, Dispatch(technician, null, [equipment.Id, equipment.Id]));
        using var missingTechnician = await DispatchAsync(client, incident.Id, Dispatch(Guid.NewGuid()));
        using var missingIncident = await DispatchAsync(client, Guid.NewGuid(), Dispatch(technician));

        Assert.Equal(HttpStatusCode.BadRequest, duplicateEquipment.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingTechnician.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingIncident.StatusCode);
        Assert.Equal(new DatabaseState(0, 0, 0, 0, 0), await StateAsync(factory, incident.CustomerId, technician, equipment.Id));
    }

    // ---- Resolve / cancel ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Resolve_marks_a_dispatched_incident_resolved_without_completing_its_work()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var incident = await ReadyIncidentAsync(client, day, [technician]);
        using var dispatchResponse = await DispatchAsync(client, incident.Id, Dispatch(technician));
        var dispatched = (await dispatchResponse.Content.ReadFromJsonAsync<IncidentDispatchResponse>(Cancellation))!;

        var resolved = await PostAsync<IncidentResponse>(client, $"/api/incidents/{incident.Id}/resolve", null);

        Assert.Equal(("Resolved", (Guid?)dispatched.WorkOrderId), (resolved.Status, resolved.WorkOrderId));
        Assert.NotNull(resolved.ResolvedAtUtc);
        Assert.Equal(resolved.ResolvedAtUtc, resolved.UpdatedAtUtc);
        Assert.Equal(["IncidentCreated", "IncidentAnalyzed", "IncidentDispatched", "IncidentResolved"], await IncidentEventTypesAsync(factory, incident.Id));

        // The work order, its visit and the assignment follow their own lifecycle: nothing completed automatically.
        Assert.Equal("Open", (await GetAsync<WorkOrderResponse>(client, $"/api/work-orders/{dispatched.WorkOrderId}")).Status);
        Assert.Equal("Planned", (await GetAsync<VisitResponse>(client, $"/api/visits/{dispatched.VisitId}")).Status);
        Assert.Equal("Active", (await GetAsync<AssignmentResponse>(client, $"/api/visits/{dispatched.VisitId}/assignment")).Status);

        using var again = await client.PostAsync($"/api/incidents/{incident.Id}/resolve", null, Cancellation);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Cancel_before_dispatch_is_allowed_and_after_dispatch_is_409_without_downstream_changes()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var notDispatched = await ReadyIncidentAsync(client, day, [technician]);
        var newOne = await CreateIncidentAsync(client, day.AddHours(1), day.AddHours(2));
        var dispatchedIncident = await ReadyIncidentAsync(client, day, [technician]);
        using var dispatchResponse = await DispatchAsync(client, dispatchedIncident.Id, Dispatch(technician));
        var dispatched = (await dispatchResponse.Content.ReadFromJsonAsync<IncidentDispatchResponse>(Cancellation))!;

        var cancelledReady = await PostAsync<IncidentResponse>(client, $"/api/incidents/{notDispatched.Id}/cancel", null);
        var cancelledNew = await PostAsync<IncidentResponse>(client, $"/api/incidents/{newOne.Id}/cancel", null);
        using var cancelDispatched = await client.PostAsync($"/api/incidents/{dispatchedIncident.Id}/cancel", null, Cancellation);

        Assert.Equal(("Cancelled", "Cancelled"), (cancelledReady.Status, cancelledNew.Status));
        Assert.Equal("IncidentCancelled", (await IncidentEventTypesAsync(factory, notDispatched.Id)).Last());
        Assert.Equal(HttpStatusCode.Conflict, cancelDispatched.StatusCode);

        var current = await GetAsync<IncidentResponse>(client, $"/api/incidents/{dispatchedIncident.Id}");
        Assert.Equal(("Dispatched", (Guid?)dispatched.WorkOrderId), (current.Status, current.WorkOrderId));
        Assert.DoesNotContain("IncidentCancelled", await IncidentEventTypesAsync(factory, dispatchedIncident.Id));
        Assert.Equal("Open", (await GetAsync<WorkOrderResponse>(client, $"/api/work-orders/{dispatched.WorkOrderId}")).Status);
        Assert.Equal("Planned", (await GetAsync<VisitResponse>(client, $"/api/visits/{dispatched.VisitId}")).Status);
        AssertSameJson(dispatched.Assignment, await GetAsync<AssignmentResponse>(client, $"/api/visits/{dispatched.VisitId}/assignment"));
        Assert.Equal(1, await ReservationCountAsync(factory, technician, ResourceType.Technician));
    }
}
