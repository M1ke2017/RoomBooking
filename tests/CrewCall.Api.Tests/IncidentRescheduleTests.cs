using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CrewCall.Contracts.Absences;
using CrewCall.Contracts.Incidents;
using CrewCall.Contracts.OperationalCalendar;
using CrewCall.Contracts.Scheduling;
using CrewCall.Contracts.Visits;
using CrewCall.Contracts.WorkingHours;
using CrewCall.Contracts.WorkOrders;
using CrewCall.Persistence;
using CrewCall.Scheduling.Incidents;
using CrewCall.Scheduling.Ports;
using CrewCall.Scheduling.Reservations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CrewCall.Api.Tests.IncidentTestKit;

namespace CrewCall.Api.Tests;

/// <summary>
/// Dynamic rescheduling (Sprint 15, ADR-0017) on the real database: proposals (priority rule, slot search, impact,
/// read-only), the manager's apply (one transaction: visit moved with its reservations, incident dispatched), stale
/// proposals and races. Times are UTC; the kit's technicians work around the clock unless a test sets hours.
/// </summary>
public sealed class IncidentRescheduleTests(CrewCallApiFactory factory)
{
    // ---- The business example ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Business_example_moves_Y_to_14_00_with_one_visit_one_customer_and_210_minutes_delay_then_apply_replans_the_day()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var a = await CreateTechnicianAsync(client);
        var x = await WorkAsync(client, a, day.AddHours(9), day.AddHours(10), "Normal");
        var y = await WorkAsync(client, a, day.AddHours(10.5), day.AddHours(12), "Normal", before: 30);
        var z = await ReadyAsync(client, day.AddHours(11.5), day.AddHours(13), "Critical", a);

        var proposal = Assert.Single(await ProposeAsync(client, z.Id, [a], after: 30));

        Assert.True(proposal.IsFeasible);
        Assert.Equal((1, z.Id, a, y.Visit.Id, (Guid?)y.Assignment.AssignmentId, "Normal"),
            (proposal.Rank, proposal.IncidentId, proposal.RecommendedTechnicianId, proposal.ConflictingVisitId.GetValueOrDefault(),
                proposal.ConflictingAssignmentId, proposal.ConflictingPriority));
        Assert.Equal((day.AddHours(11.5), day.AddHours(13)), (proposal.UrgentVisitStartUtc, proposal.UrgentVisitEndUtc));
        Assert.Equal((day.AddHours(14), day.AddHours(15.5)), (proposal.ProposedNewStartUtc!.Value, proposal.ProposedNewEndUtc!.Value));
        Assert.Equal((1, 1, 210, true), (proposal.Impact.AffectedVisitCount, proposal.Impact.AffectedCustomerCount, proposal.Impact.DelayMinutes, proposal.Impact.ConflictResolved));
        var affected = Assert.Single(proposal.Impact.AffectedVisits);
        Assert.Equal(
            new AffectedVisit(y.Visit.Id, y.WorkOrder.CustomerId, y.WorkOrder.SiteId, day.AddHours(10.5), day.AddHours(12), day.AddHours(14), day.AddHours(15.5), 210),
            affected);
        Assert.Contains(proposal.Impact.Warnings, warning => warning.Contains(y.WorkOrder.CustomerId.ToString()) && warning.Contains("210"));

        using var response = await ApplyAsync(client, z.Id, proposal);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var applied = (await response.Content.ReadFromJsonAsync<ApplyRescheduleResponse>(Cancellation))!;
        Assert.Equal(affected, applied.MovedVisit);
        Assert.Equal(("Dispatched", a), (applied.Dispatch.Incident.Status, applied.Dispatch.Assignment.TechnicianId));

        // The calendar shows the new plan: X untouched, the urgent visit, Y in its new slot (same visit, same assignment).
        var calendar = await CalendarAsync(client, a, day);
        Assert.Equal(
            [
                (x.Visit.Id, day.AddHours(9), day.AddHours(10), (Guid?)x.Assignment.AssignmentId),
                (applied.Dispatch.VisitId, day.AddHours(11.5), day.AddHours(13), (Guid?)applied.Dispatch.Assignment.AssignmentId),
                (y.Visit.Id, day.AddHours(14), day.AddHours(15.5), (Guid?)y.Assignment.AssignmentId)
            ],
            calendar.Items.Select(item => (item.VisitId, item.VisitStartUtc, item.VisitEndUtc, item.AssignmentId)));
    }

    // ---- Proposal ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_free_technician_gives_a_direct_proposal_without_any_move_ranked_before_a_move()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var busy = await CreateTechnicianAsync(client);
        var free = await CreateTechnicianAsync(client);
        await WorkAsync(client, busy, day.AddHours(10), day.AddHours(11), "Low");
        var incident = await ReadyAsync(client, day.AddHours(10), day.AddHours(11), "Urgent", busy, free);

        var proposals = await ProposeAsync(client, incident.Id, [busy, free]);

        Assert.Equal([(1, free, true, 0), (2, busy, true, 1)],
            proposals.Select(p => (p.Rank, p.RecommendedTechnicianId, p.IsFeasible, p.Impact.AffectedVisitCount)));
        var direct = proposals[0];
        Assert.Equal((null, null, null, null), (direct.ConflictingVisitId, direct.ConflictingAssignmentId, direct.ProposedNewStartUtc, direct.ProposedNewEndUtc));
        Assert.Equal((0, 0, 0, true), (direct.Impact.AffectedCustomerCount, direct.Impact.DelayMinutes, direct.Impact.AffectedVisits.Count, direct.Impact.ConflictResolved));
    }

    [Fact]
    public async Task Proposals_are_ordered_by_smallest_delay_and_capped_at_five()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technicians = new List<Guid>();
        var expected = new List<(Guid Technician, int Delay)>();

        // Six busy technicians: each one's visit (2 hours, from 10:00) ends later, so moving it after the incident costs less.
        for (var i = 0; i < 6; i++)
        {
            var technician = await CreateTechnicianAsync(client);
            var start = day.AddHours(10).AddMinutes(-15 * i);
            await WorkAsync(client, technician, start, start.AddHours(2), "Normal");
            technicians.Add(technician);
            expected.Add((technician, (int)(day.AddHours(11) - start).TotalMinutes));
        }

        var incident = await ReadyAsync(client, day.AddHours(10), day.AddHours(11), "Urgent", [.. technicians]);

        var proposals = await ProposeAsync(client, incident.Id, technicians);

        Assert.Equal(UrgentIncidentService.MaxRescheduleProposals, proposals.Length);
        Assert.Equal([1, 2, 3, 4, 5], proposals.Select(p => p.Rank));
        Assert.Equal(expected.OrderBy(e => e.Delay).Take(5), proposals.Select(p => (p.RecommendedTechnicianId, p.Impact.DelayMinutes)));
    }

    [Theory]
    [InlineData("Critical", "High", true)]
    [InlineData("Urgent", "High", true)]
    [InlineData("High", "Normal", true)]
    [InlineData("High", "Low", true)]
    [InlineData("High", "High", false)]
    [InlineData("High", "Urgent", false)]
    [InlineData("Urgent", "Urgent", false)]
    [InlineData("Critical", "Urgent", false)]
    public async Task Only_strictly_lower_priority_work_is_moved(string incidentPriority, string visitPriority, bool movable)
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var visit = await WorkAsync(client, technician, day.AddHours(10), day.AddHours(11), visitPriority);
        var incident = await ReadyAsync(client, day.AddHours(10), day.AddHours(11), incidentPriority, technician);

        var proposal = Assert.Single(await ProposeAsync(client, incident.Id, [technician]));

        Assert.Equal((movable, (Guid?)visit.Visit.Id, visitPriority), (proposal.IsFeasible, proposal.ConflictingVisitId, proposal.ConflictingPriority));
        if (movable)
        {
            Assert.Equal((day.AddHours(11), 60), (proposal.ProposedNewStartUtc!.Value, proposal.Impact.DelayMinutes));
        }
        else
        {
            Assert.Null(proposal.ProposedNewStartUtc);
            Assert.False(proposal.Impact.ConflictResolved);
            Assert.Contains("may only move lower-priority work", Assert.Single(proposal.Impact.Warnings));

            // And apply refuses it too, whatever the request says.
            using var response = await ApplyAsync(client, incident.Id, proposal with
            {
                ConflictingVisitId = visit.Visit.Id, ProposedNewStartUtc = day.AddHours(11), ProposedNewEndUtc = day.AddHours(12)
            });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("Proposal out of date", (await ProblemAsync(response)).Title);
        }
    }

    [Fact]
    public async Task Unqualified_unavailable_and_inactive_technicians_get_no_proposal()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var code = UniqueCode("RS");
        var skill = await CreateSkillAsync(client, code);
        var qualified = await CreateTechnicianAsync(client, true, skill);
        var unskilled = await CreateTechnicianAsync(client);
        var absent = await CreateTechnicianAsync(client, true, skill);
        var inactive = await CreateTechnicianAsync(client, false, skill);
        await PostAsync<AbsenceResponse>(client, $"/api/technicians/{absent}/absences",
            new CreateAbsenceRequest(day.AddHours(9), day.AddHours(13), "SickLeave", null));
        var incident = await CreateIncidentAsync(client, day.AddHours(10), day.AddHours(11), "Urgent", code);
        await AnalyzeAsync(client, incident.Id, qualified, unskilled, absent, inactive);

        var proposals = await ProposeAsync(client, incident.Id, [qualified, unskilled, absent, inactive]);

        Assert.Equal([qualified], proposals.Select(p => p.RecommendedTechnicianId));
    }

    [Fact]
    public async Task A_vehicle_held_by_a_higher_priority_visit_or_two_visits_in_the_way_give_an_infeasible_proposal()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var other = await CreateTechnicianAsync(client);
        var vehicle = (await CreateVehicleAsync(client)).Id;
        await WorkAsync(client, technician, day.AddHours(10), day.AddHours(11), "Low");
        var urgentWork = await WorkAsync(client, other, day.AddHours(10), day.AddHours(11), "Urgent", vehicleId: vehicle);
        var incident = await ReadyAsync(client, day.AddHours(10), day.AddHours(11), "Critical", technician);

        // Only the vehicle is in the way, and its visit outranks nothing the incident may move.
        var vehicleOnly = Assert.Single(await ProposeAsync(client, incident.Id, [other], vehicleId: vehicle));
        Assert.Equal((false, (Guid?)urgentWork.Visit.Id), (vehicleOnly.IsFeasible, vehicleOnly.ConflictingVisitId));

        // The technician's own visit and the vehicle's: a proposal moves at most one visit.
        var two = Assert.Single(await ProposeAsync(client, incident.Id, [technician], vehicleId: vehicle));
        Assert.False(two.IsFeasible);
        Assert.Null(two.ConflictingVisitId);
        Assert.Contains("2 visits are in the way", Assert.Single(two.Impact.Warnings));
    }

    [Fact]
    public async Task Equipment_held_by_a_higher_priority_visit_gives_an_infeasible_proposal_and_free_equipment_a_direct_one()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var other = await CreateTechnicianAsync(client);
        var held = (await CreateEquipmentAsync(client)).Id;
        var free = (await CreateEquipmentAsync(client)).Id;
        var urgentWork = await WorkAsync(client, other, day.AddHours(9.5), day.AddHours(10.5), "Urgent", equipmentIds: [held]);
        var incident = await ReadyAsync(client, day.AddHours(10), day.AddHours(11), "Urgent", technician);

        var blocked = Assert.Single(await PostAsync<RescheduleProposal[]>(client, $"/api/incidents/{incident.Id}/reschedule-proposal",
            new RescheduleProposalRequest([technician], null, [held], null, null)));
        var direct = Assert.Single(await PostAsync<RescheduleProposal[]>(client, $"/api/incidents/{incident.Id}/reschedule-proposal",
            new RescheduleProposalRequest([technician], null, [free], null, null)));

        Assert.Equal((false, (Guid?)urgentWork.Visit.Id, "Urgent"), (blocked.IsFeasible, blocked.ConflictingVisitId, blocked.ConflictingPriority));
        Assert.Equal((true, (Guid?)null), (direct.IsFeasible, direct.ConflictingVisitId));
        Assert.Equal([free], direct.EquipmentIds);
    }

    [Fact]
    public async Task The_moved_visit_keeps_its_own_vehicle_and_equipment_and_its_new_slot_avoids_their_other_reservations()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var other = await CreateTechnicianAsync(client);
        var vehicle = (await CreateVehicleAsync(client)).Id;
        var equipment = (await CreateEquipmentAsync(client)).Id;
        var moved = await WorkAsync(client, technician, day.AddHours(10), day.AddHours(11), "Normal", vehicleId: vehicle, equipmentIds: [equipment]);

        // Right after the incident the vehicle is busy elsewhere until 12:00, and the equipment until 13:00.
        await WorkAsync(client, other, day.AddHours(11), day.AddHours(12), "Normal", vehicleId: vehicle);
        var third = await CreateTechnicianAsync(client);
        await WorkAsync(client, third, day.AddHours(12), day.AddHours(13), "Normal", equipmentIds: [equipment]);
        var incident = await ReadyAsync(client, day.AddHours(10), day.AddHours(11), "Urgent", technician);

        var proposal = Assert.Single(await ProposeAsync(client, incident.Id, [technician]));

        Assert.Equal((true, (Guid?)moved.Visit.Id), (proposal.IsFeasible, proposal.ConflictingVisitId));
        Assert.Equal((day.AddHours(13), day.AddHours(14)), (proposal.ProposedNewStartUtc!.Value, proposal.ProposedNewEndUtc!.Value));

        // Apply moves every reservation of the visit's assignment: technician, vehicle and equipment.
        using var response = await ApplyAsync(client, incident.Id, proposal);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(
            [(ResourceType.Technician, day.AddHours(13), day.AddHours(14)), (ResourceType.Vehicle, day.AddHours(13), day.AddHours(14)), (ResourceType.Equipment, day.AddHours(13), day.AddHours(14))],
            await ReservationsOfAsync(moved.Assignment.AssignmentId));
    }

    [Fact]
    public async Task An_absence_pushes_the_new_slot_past_it()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        await WorkAsync(client, technician, day.AddHours(10), day.AddHours(11.5), "Normal");
        await PostAsync<AbsenceResponse>(client, $"/api/technicians/{technician}/absences",
            new CreateAbsenceRequest(day.AddHours(12), day.AddHours(16), "Training", null));
        var incident = await ReadyAsync(client, day.AddHours(10), day.AddHours(12), "Urgent", technician);

        var proposal = Assert.Single(await ProposeAsync(client, incident.Id, [technician]));

        Assert.Equal((day.AddHours(16), day.AddHours(17.5), 360), (proposal.ProposedNewStartUtc!.Value, proposal.ProposedNewEndUtc!.Value, proposal.Impact.DelayMinutes));
    }

    [Fact]
    public async Task The_first_free_slot_is_after_the_next_reservation_and_its_travel_buffer()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        await WorkAsync(client, technician, day.AddHours(10), day.AddHours(11), "Normal", before: 15);
        await WorkAsync(client, technician, day.AddHours(12), day.AddHours(13), "High", after: 30);
        var incident = await ReadyAsync(client, day.AddHours(10), day.AddHours(11), "Urgent", technician);

        var proposal = Assert.Single(await ProposeAsync(client, incident.Id, [technician], after: 15));

        // 11:00 + 15 (incident after) + 15 (moved before) = 11:30, but 11:30–12:30 meets the next visit; after it 13:00 +
        // 30 (its after buffer) + 15 (moved before) = 13:45.
        Assert.Equal((day.AddHours(13.75), day.AddHours(14.75), 225), (proposal.ProposedNewStartUtc!.Value, proposal.ProposedNewEndUtc!.Value, proposal.Impact.DelayMinutes));
    }

    [Fact]
    public async Task Working_hours_move_the_visit_to_the_next_morning_with_a_warning()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await ApiTestData.CreateTechnicianAsync(client, timeZoneId: "UTC");
        foreach (var weekday in Enum.GetNames<DayOfWeek>())
        {
            await PostAsync<WorkingHoursResponse>(client, $"/api/technicians/{technician}/working-hours", new CreateWorkingHoursRequest(weekday, "08:00", "16:00"));
        }

        await WorkAsync(client, technician, day.AddHours(13), day.AddHours(14.5), "Normal", before: 30);
        var incident = await ReadyAsync(client, day.AddHours(14), day.AddHours(15.5), "Urgent", technician);

        var proposal = Assert.Single(await ProposeAsync(client, incident.Id, [technician], after: 30));

        Assert.Equal((day.AddDays(1).AddHours(8), day.AddDays(1).AddHours(9.5)), (proposal.ProposedNewStartUtc!.Value, proposal.ProposedNewEndUtc!.Value));
        Assert.Equal(19 * 60, proposal.Impact.DelayMinutes);
        Assert.Contains(proposal.Impact.Warnings, warning => warning.Contains("moves to another day"));
    }

    [Fact]
    public async Task A_proposal_writes_nothing()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var moved = await WorkAsync(client, technician, day.AddHours(10), day.AddHours(11), "Normal");
        var incident = await ReadyAsync(client, day.AddHours(10), day.AddHours(11), "Urgent", technician);
        var before = await SnapshotAsync(technician, moved.Visit.Id, incident.Id);

        await ProposeAsync(client, incident.Id, [technician]);
        await ProposeAsync(client, incident.Id, [technician]);

        Assert.Equal(before, await SnapshotAsync(technician, moved.Visit.Id, incident.Id));
        Assert.Equal("ReadyForDispatch", (await GetAsync<IncidentResponse>(client, $"/api/incidents/{incident.Id}")).Status);
        Assert.Equal(day.AddHours(10), (await GetAsync<VisitResponse>(client, $"/api/visits/{moved.Visit.Id}")).Start);
    }

    [Fact]
    public async Task Proposal_returns_404_for_a_missing_incident_and_409_when_not_ready()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var incident = await CreateIncidentAsync(client, day.AddHours(10), day.AddHours(11));

        using var missing = await client.PostAsJsonAsync($"/api/incidents/{Guid.NewGuid()}/reschedule-proposal", (object?)null, Cancellation);
        using var notReady = await client.PostAsJsonAsync($"/api/incidents/{incident.Id}/reschedule-proposal", Request([technician]), Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, notReady.StatusCode);
    }

    // ---- Apply ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Apply_moves_the_visit_keeps_its_identity_and_assignment_dispatches_the_incident_and_records_history_and_outbox()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var y = await WorkAsync(client, technician, day.AddHours(10), day.AddHours(11), "Normal", before: 15, after: 15);
        var incident = await ReadyAsync(client, day.AddHours(10), day.AddHours(11), "Critical", technician);
        var proposal = Assert.Single(await ProposeAsync(client, incident.Id, [technician]));
        var (newStart, newEnd) = (day.AddHours(11.25), day.AddHours(12.25));
        Assert.Equal((newStart, newEnd), (proposal.ProposedNewStartUtc!.Value, proposal.ProposedNewEndUtc!.Value));

        using var response = await ApplyAsync(client, incident.Id, proposal);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var applied = (await response.Content.ReadFromJsonAsync<ApplyRescheduleResponse>(Cancellation))!;
        Assert.Equal($"/api/work-orders/{applied.Dispatch.WorkOrderId}", response.Headers.Location?.OriginalString);

        // The same visit, in its new window, still Planned and with the same (active) assignment, its claim moved.
        var visit = await GetAsync<VisitResponse>(client, $"/api/visits/{y.Visit.Id}");
        Assert.Equal((y.Visit.Id, y.Visit.WorkOrderId, newStart, newEnd, "Planned"), (visit.Id, visit.WorkOrderId, visit.Start, visit.End, visit.Status));
        var assignment = await GetAsync<AssignmentResponse>(client, $"/api/visits/{y.Visit.Id}/assignment");
        Assert.Equal((y.Assignment.AssignmentId, "Active", newStart.AddMinutes(-15), newEnd.AddMinutes(15)),
            (assignment.AssignmentId, assignment.Status, assignment.ClaimedStart, assignment.ClaimedEnd));
        Assert.Single((await GetAsync<AssignmentHistoryResponse>(client, $"/api/visits/{y.Visit.Id}/assignments/history")).Assignments);
        Assert.Equal([(ResourceType.Technician, newStart.AddMinutes(-15), newEnd.AddMinutes(15))], await ReservationsOfAsync(y.Assignment.AssignmentId));

        // The incident is dispatched to the technician for its own window.
        var current = await GetAsync<IncidentResponse>(client, $"/api/incidents/{incident.Id}");
        Assert.Equal(("Dispatched", (Guid?)applied.Dispatch.WorkOrderId), (current.Status, current.WorkOrderId));
        Assert.Equal((technician, day.AddHours(10), day.AddHours(11)),
            (applied.Dispatch.Assignment.TechnicianId, applied.Dispatch.Assignment.ClaimedStart, applied.Dispatch.Assignment.ClaimedEnd));

        // History: VisitRescheduled on the visit; RescheduleApplied then IncidentDispatched on the incident.
        var rescheduled = Assert.Single(await EventsAsync(client, "visit", y.Visit.Id), e => e.EventType == "VisitRescheduled");
        Assert.Equal(
            (day.AddHours(10), day.AddHours(11), newStart, newEnd, "UrgentIncident", incident.Id),
            (rescheduled.Payload.GetProperty("oldStart").GetDateTimeOffset(), rescheduled.Payload.GetProperty("oldEnd").GetDateTimeOffset(),
                rescheduled.Payload.GetProperty("newStart").GetDateTimeOffset(), rescheduled.Payload.GetProperty("newEnd").GetDateTimeOffset(),
                rescheduled.Payload.GetProperty("reason").GetString(), rescheduled.Payload.GetProperty("incidentId").GetGuid()));
        Assert.Equal(["IncidentCreated", "IncidentAnalyzed", "RescheduleApplied", "IncidentDispatched"], await IncidentEventTypesAsync(factory, incident.Id));
        var summary = Assert.Single(await EventsAsync(client, "incident", incident.Id), e => e.EventType == "RescheduleApplied");
        Assert.Equal((y.Visit.Id, applied.Dispatch.VisitId, 75),
            (summary.Payload.GetProperty("movedVisitId").GetGuid(), summary.Payload.GetProperty("urgentVisitId").GetGuid(), summary.Payload.GetProperty("delayMinutes").GetInt32()));

        // Integration event: visit.rescheduled v1 in the outbox, committed with the rest.
        using var message = await OutboxPayloadAsync("visit.rescheduled", y.Visit.Id);
        Assert.Equal((y.Visit.Id, newStart, "UrgentIncident", incident.Id),
            (message.RootElement.GetProperty("visitId").GetGuid(), message.RootElement.GetProperty("newStartUtc").GetDateTimeOffset(),
                message.RootElement.GetProperty("reason").GetString(), message.RootElement.GetProperty("incidentId").GetGuid()));
    }

    [Fact]
    public async Task Apply_of_a_proposal_without_a_move_is_a_plain_dispatch()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var incident = await ReadyAsync(client, day.AddHours(10), day.AddHours(11), "Urgent", technician);
        var proposal = Assert.Single(await ProposeAsync(client, incident.Id, [technician]));

        using var response = await ApplyAsync(client, incident.Id, proposal);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var applied = (await response.Content.ReadFromJsonAsync<ApplyRescheduleResponse>(Cancellation))!;
        Assert.Null(applied.MovedVisit);
        Assert.Equal(["IncidentCreated", "IncidentAnalyzed", "IncidentDispatched"], await IncidentEventTypesAsync(factory, incident.Id));
    }

    [Fact]
    public async Task Apply_validates_the_request()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var y = await WorkAsync(client, technician, day.AddHours(10), day.AddHours(11), "Normal");
        var incident = await ReadyAsync(client, day.AddHours(10), day.AddHours(11), "Urgent", technician);

        async Task<string[]> ErrorsAsync(ApplyRescheduleRequest request)
        {
            using var response = await client.PostAsJsonAsync($"/api/incidents/{incident.Id}/reschedule-proposal/apply", request, Cancellation);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return [.. (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Cancellation))!.Errors.Keys.Order()];
        }

        Assert.Equal(["movedVisitNewEndUtc", "movedVisitNewStartUtc", "technicianId"],
            await ErrorsAsync(new ApplyRescheduleRequest(null, null, null, null, null, y.Visit.Id, null, null)));
        Assert.Equal(["movedVisitNewEndUtc"],
            await ErrorsAsync(new ApplyRescheduleRequest(technician, null, null, null, null, y.Visit.Id, day.AddHours(12), day.AddHours(12))));
        Assert.Equal(["movedVisitId"],
            await ErrorsAsync(new ApplyRescheduleRequest(technician, null, null, null, null, null, day.AddHours(12), day.AddHours(13))));

        // The moved visit keeps its duration.
        Assert.Equal(["movedVisitNewEndUtc"],
            await ErrorsAsync(new ApplyRescheduleRequest(technician, null, null, null, null, y.Visit.Id, day.AddHours(12), day.AddHours(14))));
        Assert.Equal("ReadyForDispatch", (await GetAsync<IncidentResponse>(client, $"/api/incidents/{incident.Id}")).Status);
    }

    // ---- Stale proposals: 409 and nothing written ----------------------------------------------------------------------

    [Fact]
    public async Task A_visit_started_since_the_proposal_makes_it_stale_and_nothing_is_written()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var y = await WorkAsync(client, technician, day.AddHours(10), day.AddHours(11), "Normal");
        var incident = await ReadyAsync(client, day.AddHours(10), day.AddHours(11), "Urgent", technician);
        var proposal = Assert.Single(await ProposeAsync(client, incident.Id, [technician]));
        (await client.PostAsJsonAsync($"/api/visits/{y.Visit.Id}/status", new ChangeVisitStatusRequest("InProgress"), Cancellation)).EnsureSuccessStatusCode();
        var before = await SnapshotAsync(technician, y.Visit.Id, incident.Id);

        using var response = await ApplyAsync(client, incident.Id, proposal);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal("Proposal out of date", problem.Title);
        Assert.Contains("InProgress", problem.Detail);
        await AssertNothingAppliedAsync(client, incident, y, day.AddHours(10), technician, before);
    }

    [Fact]
    public async Task A_new_slot_taken_since_the_proposal_is_refused_with_409_and_nothing_is_written()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var y = await WorkAsync(client, technician, day.AddHours(10), day.AddHours(11), "Normal");
        var incident = await ReadyAsync(client, day.AddHours(10), day.AddHours(11), "Urgent", technician);
        var proposal = Assert.Single(await ProposeAsync(client, incident.Id, [technician]));
        var competitor = await WorkAsync(client, technician, proposal.ProposedNewStartUtc!.Value, proposal.ProposedNewEndUtc!.Value, "Low");
        var before = await SnapshotAsync(technician, y.Visit.Id, incident.Id);

        using var response = await ApplyAsync(client, incident.Id, proposal);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal("Resources not available", problem.Title);
        Assert.Equal(competitor.Visit.Id, ((JsonElement)problem.Extensions["impact"]!)[0].GetProperty("conflictingVisitId").GetGuid());
        await AssertNothingAppliedAsync(client, incident, y, day.AddHours(10), technician, before);

        // A fresh proposal accounts for it.
        var fresh = Assert.Single(await ProposeAsync(client, incident.Id, [technician]));
        Assert.Equal(proposal.ProposedNewEndUtc, fresh.ProposedNewStartUtc);
    }

    [Fact]
    public async Task Applying_the_same_proposal_twice_gives_409_the_second_time()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var y = await WorkAsync(client, technician, day.AddHours(10), day.AddHours(11), "Normal");
        var incident = await ReadyAsync(client, day.AddHours(10), day.AddHours(11), "Urgent", technician);
        var proposal = Assert.Single(await ProposeAsync(client, incident.Id, [technician]));

        using var first = await ApplyAsync(client, incident.Id, proposal);
        using var second = await ApplyAsync(client, incident.Id, proposal);

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Conflict), (first.StatusCode, second.StatusCode));
        Assert.Equal("Incident already dispatched", (await ProblemAsync(second)).Title);
        Assert.Single(await EventsAsync(client, "visit", y.Visit.Id), e => e.EventType == "VisitRescheduled");
    }

    // ---- Concurrency on the real database ----------------------------------------------------------------------------

    [Fact]
    public async Task Two_concurrent_applies_give_one_success_and_one_409_and_the_visit_moves_once()
    {
        var gate = new DispatchStagingGate();
        await using var gated = Gated(gate);
        using var client = gated.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var y = await WorkAsync(client, technician, day.AddHours(10), day.AddHours(11), "Normal");
        var incident = await ReadyAsync(client, day.AddHours(10), day.AddHours(11), "Urgent", technician);
        var proposal = Assert.Single(await ProposeAsync(client, incident.Id, [technician]));
        var hold = gate.HoldFirst(incident.Id);

        // The first apply has moved the visit (saved, not committed) and waits before dispatching; the second one then
        // runs into the visit's row: the database decides.
        var first = ApplyAsync(client, incident.Id, proposal);
        await hold.Arrived.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        var second = ApplyAsync(client, incident.Id, proposal);
        await WaitForLockWaitAsync();
        hold.Release();
        var responses = await Task.WhenAll(first, second);

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        Assert.Equal("Incident already dispatched", (await ProblemAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict))).Title);
        Assert.Single(await EventsAsync(client, "visit", y.Visit.Id), e => e.EventType == "VisitRescheduled");
        Assert.Equal(["IncidentCreated", "IncidentAnalyzed", "RescheduleApplied", "IncidentDispatched"], await IncidentEventTypesAsync(factory, incident.Id));
        Assert.Equal(2, await ReservationCountAsync(factory, technician, ResourceType.Technician));

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task A_resource_of_the_urgent_visit_taken_while_apply_waits_rolls_back_the_move_too()
    {
        var gate = new DispatchStagingGate();
        await using var gated = Gated(gate);
        using var client = gated.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var other = await CreateTechnicianAsync(client);
        var vehicle = (await CreateVehicleAsync(client)).Id;
        var y = await WorkAsync(client, technician, day.AddHours(10), day.AddHours(11), "Normal");
        var incident = await ReadyAsync(client, day.AddHours(10), day.AddHours(11), "Urgent", technician);
        var proposal = Assert.Single(await ProposeAsync(client, incident.Id, [technician], vehicleId: vehicle));
        var hold = gate.HoldFirst(incident.Id);

        var apply = ApplyAsync(client, incident.Id, proposal);
        await hold.Arrived.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);

        // While apply waits (visit moved, not committed; urgent check passed), other work takes the urgent visit's vehicle.
        var competitor = await WorkAsync(client, other, day.AddHours(10.5), day.AddHours(10.75), "Low", vehicleId: vehicle);
        hold.Release();
        using var response = await apply;

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal("Resources not available", problem.Title);
        Assert.Equal(competitor.Visit.Id, ((JsonElement)problem.Extensions["impact"]!)[0].GetProperty("conflictingVisitId").GetGuid());
        Assert.Equal(day.AddHours(10), (await GetAsync<VisitResponse>(client, $"/api/visits/{y.Visit.Id}")).Start);
        Assert.Equal([(ResourceType.Technician, day.AddHours(10), day.AddHours(11))], await ReservationsOfAsync(y.Assignment.AssignmentId));
        Assert.DoesNotContain(await EventsAsync(client, "visit", y.Visit.Id), e => e.EventType == "VisitRescheduled");
        Assert.Equal(["IncidentCreated", "IncidentAnalyzed"], await IncidentEventTypesAsync(factory, incident.Id));
        Assert.Equal(0, await OutboxCountAsync("visit.rescheduled", y.Visit.Id));
        Assert.Equal(1, await ReservationCountAsync(factory, vehicle, ResourceType.Vehicle));
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    /// <summary>Existing work: a work order of the priority, its visit over [start, end), assigned with the buffers.</summary>
    private static async Task<(WorkOrderResponse WorkOrder, VisitResponse Visit, AssignmentResponse Assignment)> WorkAsync(
        HttpClient client, Guid technicianId, DateTimeOffset start, DateTimeOffset end, string priority,
        int before = 0, int after = 0, Guid? vehicleId = null, Guid[]? equipmentIds = null)
    {
        var customerId = await ApiTestData.CreateCustomerAsync(client);
        var siteId = await ApiTestData.CreateSiteAsync(client, customerId);
        var workOrder = await PostAsync<WorkOrderResponse>(client, "/api/work-orders",
            new CreateWorkOrderRequest(customerId, siteId, "Planned maintenance", null, priority));
        var visit = await PostAsync<VisitResponse>(client, $"/api/work-orders/{workOrder.Id}/visits", new CreateVisitRequest(start, end, null));
        var assignment = await PostAsync<AssignmentResponse>(client, $"/api/visits/{visit.Id}/assignment",
            new CreateAssignmentRequest(technicianId, vehicleId, equipmentIds, null, before, after));
        return (workOrder, visit, assignment);
    }

    private static async Task<IncidentResponse> ReadyAsync(
        HttpClient client, DateTimeOffset start, DateTimeOffset end, string priority, params Guid[] candidates)
    {
        var incident = await CreateIncidentAsync(client, start, end, priority);
        await AnalyzeAsync(client, incident.Id, candidates);
        return incident;
    }

    private static RescheduleProposalRequest Request(IReadOnlyCollection<Guid> candidates, Guid? vehicleId = null, int? after = null) =>
        new(candidates, vehicleId, null, null, after);

    private static Task<RescheduleProposal[]> ProposeAsync(
        HttpClient client, Guid incidentId, IReadOnlyCollection<Guid> candidates, Guid? vehicleId = null, int? after = null) =>
        PostAsync<RescheduleProposal[]>(client, $"/api/incidents/{incidentId}/reschedule-proposal", Request(candidates, vehicleId, after));

    /// <summary>The manager accepts the proposal as it was returned.</summary>
    private static Task<HttpResponseMessage> ApplyAsync(HttpClient client, Guid incidentId, RescheduleProposal proposal) =>
        client.PostAsJsonAsync(
            $"/api/incidents/{incidentId}/reschedule-proposal/apply",
            new ApplyRescheduleRequest(
                proposal.RecommendedTechnicianId, proposal.VehicleId, proposal.EquipmentIds,
                proposal.TravelBufferBeforeMinutes, proposal.TravelBufferAfterMinutes,
                proposal.ConflictingVisitId, proposal.ProposedNewStartUtc, proposal.ProposedNewEndUtc),
            Cancellation);

    private static async Task<ProblemDetails> ProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDetails>(Cancellation))!;

    private static Task<OperationalCalendarResponse> CalendarAsync(HttpClient client, Guid technicianId, DateTimeOffset day) =>
        GetAsync<OperationalCalendarResponse>(client,
            $"/api/operational-calendar?start={Uri.EscapeDataString(day.ToString("O"))}&end={Uri.EscapeDataString(day.AddDays(1).ToString("O"))}"
            + $"&timeZoneId=UTC&perspective=technician&perspectiveId={technicianId}");

    private async Task AssertNothingAppliedAsync(
        HttpClient client, IncidentResponse incident, (WorkOrderResponse WorkOrder, VisitResponse Visit, AssignmentResponse Assignment) moved,
        DateTimeOffset movedStart, Guid technicianId, Snapshot before)
    {
        var current = await GetAsync<IncidentResponse>(client, $"/api/incidents/{incident.Id}");
        Assert.Equal(("ReadyForDispatch", (Guid?)null), (current.Status, current.WorkOrderId));
        Assert.Equal(movedStart, (await GetAsync<VisitResponse>(client, $"/api/visits/{moved.Visit.Id}")).Start);
        Assert.Equal(moved.Assignment.ClaimedStart, (await GetAsync<AssignmentResponse>(client, $"/api/visits/{moved.Visit.Id}/assignment")).ClaimedStart);
        Assert.Equal(["IncidentCreated", "IncidentAnalyzed"], await IncidentEventTypesAsync(factory, incident.Id));
        Assert.Equal(new DatabaseState(0, 0, 0, 0, 0), await StateAsync(factory, incident.CustomerId));
        Assert.Equal(before, await SnapshotAsync(technicianId, moved.Visit.Id, incident.Id));
    }

    private async Task<(ResourceType Type, DateTimeOffset Start, DateTimeOffset End)[]> ReservationsOfAsync(Guid assignmentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().ResourceReservations
            .Where(r => r.AssignmentId == assignmentId)
            .Select(r => new { r.ResourceType, r.Start, r.End })
            .ToListAsync(Cancellation);
        return [.. rows.OrderBy(r => r.ResourceType).Select(r => (r.ResourceType, r.Start, r.End))];
    }

    /// <summary>What apply or a proposal could write for this technician, visit and incident (other tests run alongside).</summary>
    private sealed record Snapshot(int Assignments, string Reservations, int Events, int Outbox, uint VisitVersion);

    private async Task<Snapshot> SnapshotAsync(Guid technicianId, Guid visitId, Guid incidentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CrewCallDbContext>();
        Guid?[] aggregates = [visitId, incidentId];
        var reservations = await db.ResourceReservations
            .Where(r => r.ResourceId == technicianId)
            .OrderBy(r => r.Start)
            .Select(r => new { r.Start, r.End })
            .ToListAsync(Cancellation);
        return new Snapshot(
            await db.Assignments.CountAsync(a => a.TechnicianId == technicianId, Cancellation),
            string.Join(", ", reservations.Select(r => $"{r.Start:O}/{r.End:O}")),
            await db.OperationalEvents.CountAsync(e => e.AggregateId == visitId || e.AggregateId == incidentId, Cancellation),
            await db.OutboxMessages.CountAsync(m => aggregates.Contains(m.AggregateId), Cancellation),
            await db.Visits.Where(v => v.Id == visitId).Select(v => EF.Property<uint>(v, "RowVersion")).SingleAsync(Cancellation));
    }

    private async Task<JsonDocument> OutboxPayloadAsync(string type, Guid aggregateId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var message = await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().OutboxMessages
            .SingleAsync(m => m.Type == type && m.AggregateId == aggregateId, Cancellation);
        Assert.Equal((1, type), (message.Version, message.RoutingKey));
        return JsonDocument.Parse(message.Payload);
    }

    private async Task<int> OutboxCountAsync(string type, Guid aggregateId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().OutboxMessages
            .CountAsync(m => m.Type == type && m.AggregateId == aggregateId, Cancellation);
    }

    /// <summary>
    /// Until some session waits on a row lock of the moved visit, its assignment or its reservations: the second apply has
    /// run into the first one's (uncommitted) move.
    /// </summary>
    private async Task WaitForLockWaitAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var waiting = await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().Database.SqlQuery<int>(
                    $@"SELECT count(*)::int AS ""Value"" FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query ~* '^\s*UPDATE (workorders\.visits|scheduling\.(assignments|resource_reservations))'")
                .SingleAsync(Cancellation);
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(50, Cancellation);
        }

        throw new TimeoutException("The second apply never waited on the visit's row.");
    }

    /// <summary>A second host on the same database whose incident port waits at the gate before staging a dispatch.</summary>
    private WebApplicationFactory<Program> Gated(DispatchStagingGate gate) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var original = services.Single(descriptor => descriptor.ServiceType == typeof(IIncidentWorkOrders));
            services.Remove(original);
            services.AddScoped<IIncidentWorkOrders>(provider => new GatedIncidentWorkOrders(
                (IIncidentWorkOrders)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!), gate));
        }));
}
