using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CrewCall.Contracts.Equipment;
using CrewCall.Contracts.Scheduling;
using CrewCall.Contracts.Vehicles;
using CrewCall.Contracts.Visits;
using CrewCall.Contracts.WorkingHours;
using CrewCall.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Api.Tests;

/// <summary>Assignments end to end: real visits (WorkOrders), technicians (Workforce), resources and PostgreSQL.</summary>
public sealed class AssignmentEndpointsTests(CrewCallApiFactory factory)
{
    // Monday 2026-07-06 08:00–09:00 UTC = 10:00–11:00 in Warsaw (UTC+2), inside 08:00–16:00 local working hours.
    private static readonly DateTimeOffset _visitStart = new(2026, 7, 6, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _visitEnd = _visitStart.AddHours(1);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object request)
    {
        using var response = await client.PostAsJsonAsync(url, request, Cancellation);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Cancellation))!;
    }

    private static async Task<Guid> CreateVisitAsync(HttpClient client)
    {
        var workOrder = await ApiTestData.CreateWorkOrderAsync(client);
        return (await PostAsync<VisitResponse>(client, $"/api/work-orders/{workOrder.Id}/visits",
            new CreateVisitRequest(_visitStart, _visitEnd, null))).Id;
    }

    private static async Task<Guid> CreateWorkingTechnicianAsync(HttpClient client)
    {
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);
        await PostAsync<WorkingHoursResponse>(client, $"/api/technicians/{technicianId}/working-hours",
            new CreateWorkingHoursRequest("Monday", "08:00", "16:00"));
        return technicianId;
    }

    private static string UniqueCode(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20].ToUpperInvariant();

    private static async Task<Guid> CreateVehicleAsync(HttpClient client) =>
        (await PostAsync<VehicleResponse>(client, "/api/vehicles", new CreateVehicleRequest(UniqueCode("KR"), "Van", null, null))).Id;

    private static async Task<Guid> CreateEquipmentAsync(HttpClient client) =>
        (await PostAsync<EquipmentResponse>(client, "/api/equipment", new CreateEquipmentRequest("Tester", UniqueCode("EQ"), null))).Id;

    private static Task<HttpResponseMessage> AssignAsync(HttpClient client, Guid visitId, CreateAssignmentRequest request) =>
        client.PostAsJsonAsync($"/api/visits/{visitId}/assignment", request, Cancellation);

    private static CreateAssignmentRequest Request(Guid technicianId, Guid? vehicleId = null, Guid[]? equipment = null, int? before = null, int? after = null) =>
        new(technicianId, vehicleId, equipment, null, before, after);

    [Fact]
    public async Task Post_assignment_returns_201_and_get_returns_it()
    {
        using var client = factory.CreateClient();
        var visitId = await CreateVisitAsync(client);
        var technicianId = await CreateWorkingTechnicianAsync(client);
        var vehicleId = await CreateVehicleAsync(client);
        var equipmentId = await CreateEquipmentAsync(client);

        using var response = await AssignAsync(client, visitId, Request(technicianId, vehicleId, [equipmentId], 15, 30));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/visits/{visitId}/assignment", response.Headers.Location?.OriginalString);
        var created = await response.Content.ReadFromJsonAsync<AssignmentResponse>(Cancellation);
        Assert.NotNull(created);
        Assert.Equal(("Active", technicianId, vehicleId, 15, 30), (created.Status, created.TechnicianId, created.VehicleId, created.TravelBufferBeforeMinutes, created.TravelBufferAfterMinutes));
        Assert.Equal([equipmentId], created.EquipmentIds);
        Assert.Equal(_visitStart.AddMinutes(-15), created.ClaimedStart);
        Assert.Equal(_visitEnd.AddMinutes(30), created.ClaimedEnd);

        var active = await client.GetFromJsonAsync<AssignmentResponse>($"/api/visits/{visitId}/assignment", Cancellation);
        Assert.Equal(created.AssignmentId, active!.AssignmentId);

        // The claim is real: the scheduling check now sees the technician as reserved for this window.
        var check = await PostAsync<SchedulingCheckResponse>(client, "/api/scheduling/check",
            new SchedulingCheckRequest(technicianId, null, null, null, _visitStart, _visitEnd, null, null, null));
        Assert.Equal("TechnicianReservationConflict", Assert.Single(check.Reasons).Code);
    }

    [Fact]
    public async Task The_assignment_event_is_written_with_its_payload()
    {
        using var client = factory.CreateClient();
        var visitId = await CreateVisitAsync(client);
        var technicianId = await CreateWorkingTechnicianAsync(client);
        var created = await PostAsync<AssignmentResponse>(client, $"/api/visits/{visitId}/assignment", Request(technicianId, after: 10));

        await using var scope = factory.Services.CreateAsyncScope();
        var events = await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().OperationalEvents
            .AsNoTracking()
            .Where(e => e.AggregateId == created.AssignmentId)
            .ToListAsync(Cancellation);

        var created_ = Assert.Single(events);
        Assert.Equal(("AssignmentCreated", "assignment"), (created_.EventType, created_.AggregateType));
        using var payload = JsonDocument.Parse(created_.PayloadJson);
        Assert.Equal(visitId, payload.RootElement.GetProperty("visitId").GetGuid());
        Assert.Equal(technicianId, payload.RootElement.GetProperty("technicianId").GetGuid());
        Assert.Equal(10, payload.RootElement.GetProperty("travelBufferAfterMinutes").GetInt32());
    }

    [Fact]
    public async Task Missing_entities_return_404_and_invalid_requests_400()
    {
        using var client = factory.CreateClient();
        var visitId = await CreateVisitAsync(client);
        var technicianId = await CreateWorkingTechnicianAsync(client);
        var equipmentId = await CreateEquipmentAsync(client);

        using var missingVisit = await AssignAsync(client, Guid.NewGuid(), Request(technicianId));
        using var missingTechnician = await AssignAsync(client, visitId, Request(Guid.NewGuid()));
        using var missingVehicle = await AssignAsync(client, visitId, Request(technicianId, Guid.NewGuid()));
        using var missingEquipment = await AssignAsync(client, visitId, Request(technicianId, equipment: [Guid.NewGuid()]));
        using var duplicateEquipment = await AssignAsync(client, visitId, Request(technicianId, equipment: [equipmentId, equipmentId]));
        using var invalidBuffer = await AssignAsync(client, visitId, Request(technicianId, before: -10));

        Assert.Equal(HttpStatusCode.NotFound, missingVisit.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingTechnician.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingVehicle.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingEquipment.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, duplicateEquipment.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidBuffer.StatusCode);
    }

    [Fact]
    public async Task Unavailable_or_unskilled_technicians_and_reserved_resources_return_409_with_reasons()
    {
        using var client = factory.CreateClient();
        var technicianId = await CreateWorkingTechnicianAsync(client);
        var vehicleId = await CreateVehicleAsync(client);
        var equipmentId = await CreateEquipmentAsync(client);
        await PostAsync<AssignmentResponse>(client, $"/api/visits/{await CreateVisitAsync(client)}/assignment",
            Request(technicianId, vehicleId, [equipmentId]));
        var noHoursTechnician = await ApiTestData.CreateTechnicianAsync(client);

        using var reserved = await AssignAsync(client, await CreateVisitAsync(client), Request(technicianId, vehicleId, [equipmentId]));
        using var unavailable = await AssignAsync(client, await CreateVisitAsync(client), Request(noHoursTechnician));
        using var unskilled = await AssignAsync(client, await CreateVisitAsync(client),
            new CreateAssignmentRequest(await CreateWorkingTechnicianAsync(client), null, null, ["NO-SUCH-SKILL"], null, null));

        Assert.Equal(HttpStatusCode.Conflict, reserved.StatusCode);
        using var problem = JsonDocument.Parse(await reserved.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal(
            ["TechnicianReservationConflict", "VehicleReservationConflict", "EquipmentReservationConflict"],
            problem.RootElement.GetProperty("reasons").EnumerateArray().Select(reason => reason.GetProperty("code").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, unskilled.StatusCode);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Cancelled")]
    public async Task Closed_visits_return_409(string finalStatus)
    {
        using var client = factory.CreateClient();
        var visitId = await CreateVisitAsync(client);
        if (finalStatus == "Completed")
        {
            (await client.PostAsJsonAsync($"/api/visits/{visitId}/status", new ChangeVisitStatusRequest("InProgress"), Cancellation)).EnsureSuccessStatusCode();
        }

        (await client.PostAsJsonAsync($"/api/visits/{visitId}/status", new ChangeVisitStatusRequest(finalStatus), Cancellation)).EnsureSuccessStatusCode();

        using var response = await AssignAsync(client, visitId, Request(await CreateWorkingTechnicianAsync(client)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_second_assignment_for_the_visit_returns_409()
    {
        using var client = factory.CreateClient();
        var visitId = await CreateVisitAsync(client);
        (await AssignAsync(client, visitId, Request(await CreateWorkingTechnicianAsync(client)))).EnsureSuccessStatusCode();

        using var second = await AssignAsync(client, visitId, Request(await CreateWorkingTechnicianAsync(client)));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Concurrent_claims_of_one_technician_give_one_201_and_one_409()
    {
        using var client = factory.CreateClient();
        var technicianId = await CreateWorkingTechnicianAsync(client);
        var firstVisit = await CreateVisitAsync(client);
        var secondVisit = await CreateVisitAsync(client);

        var responses = await Task.WhenAll(
            AssignAsync(client, firstVisit, Request(technicianId)),
            AssignAsync(client, secondVisit, Request(technicianId)));

        Assert.Equal(
            [HttpStatusCode.Created, HttpStatusCode.Conflict],
            responses.Select(response => response.StatusCode).Order());
        var loserVisit = responses[0].StatusCode == HttpStatusCode.Conflict ? firstVisit : secondVisit;
        using var loserAssignment = await client.GetAsync($"/api/visits/{loserVisit}/assignment", Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, loserAssignment.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<AssignmentHistoryResponse>($"/api/visits/{loserVisit}/assignments/history", Cancellation))!.Assignments);

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Reassign_returns_200_and_history_shows_both_assignments()
    {
        using var client = factory.CreateClient();
        var visitId = await CreateVisitAsync(client);
        var oldTechnician = await CreateWorkingTechnicianAsync(client);
        var newTechnician = await CreateWorkingTechnicianAsync(client);
        var first = await PostAsync<AssignmentResponse>(client, $"/api/visits/{visitId}/assignment", Request(oldTechnician));

        using var response = await client.PostAsJsonAsync($"/api/visits/{visitId}/assignment/reassign",
            new ReassignAssignmentRequest(newTechnician, null, null, null, 0, 15), Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var second = await response.Content.ReadFromJsonAsync<AssignmentResponse>(Cancellation);
        Assert.Equal(("Active", newTechnician), (second!.Status, second.TechnicianId));

        var history = await client.GetFromJsonAsync<AssignmentHistoryResponse>($"/api/visits/{visitId}/assignments/history", Cancellation);
        Assert.Equal([first.AssignmentId, second.AssignmentId], history!.Assignments.Select(assignment => assignment.AssignmentId));
        Assert.Equal(["Replaced", "Active"], history.Assignments.Select(assignment => assignment.Status));
        Assert.Equal(second.AssignmentId, history.Assignments[0].ReplacedByAssignmentId);

        // The old technician is free again; the new one is reserved.
        (await AssignAsync(client, await CreateVisitAsync(client), Request(oldTechnician))).EnsureSuccessStatusCode();
        using var newTechnicianBusy = await AssignAsync(client, await CreateVisitAsync(client), Request(newTechnician));
        Assert.Equal(HttpStatusCode.Conflict, newTechnicianBusy.StatusCode);
    }

    [Fact]
    public async Task Reassign_to_a_resource_reserved_by_another_visit_returns_409_and_keeps_the_old_assignment()
    {
        using var client = factory.CreateClient();
        var visitId = await CreateVisitAsync(client);
        var busyTechnician = await CreateWorkingTechnicianAsync(client);
        await PostAsync<AssignmentResponse>(client, $"/api/visits/{await CreateVisitAsync(client)}/assignment", Request(busyTechnician));
        var first = await PostAsync<AssignmentResponse>(client, $"/api/visits/{visitId}/assignment", Request(await CreateWorkingTechnicianAsync(client)));

        using var response = await client.PostAsJsonAsync($"/api/visits/{visitId}/assignment/reassign",
            new ReassignAssignmentRequest(busyTechnician, null, null, null, null, null), Cancellation);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var active = await client.GetFromJsonAsync<AssignmentResponse>($"/api/visits/{visitId}/assignment", Cancellation);
        Assert.Equal((first.AssignmentId, "Active"), (active!.AssignmentId, active.Status));
    }

    [Fact]
    public async Task Cancel_returns_204_keeps_history_and_is_idempotent()
    {
        using var client = factory.CreateClient();
        var visitId = await CreateVisitAsync(client);
        var technicianId = await CreateWorkingTechnicianAsync(client);
        var assignment = await PostAsync<AssignmentResponse>(client, $"/api/visits/{visitId}/assignment", Request(technicianId));

        using var cancelled = await client.DeleteAsync($"/api/visits/{visitId}/assignment", Cancellation);
        using var again = await client.DeleteAsync($"/api/visits/{visitId}/assignment", Cancellation);
        using var missingVisit = await client.DeleteAsync($"/api/visits/{Guid.NewGuid()}/assignment", Cancellation);
        using var active = await client.GetAsync($"/api/visits/{visitId}/assignment", Cancellation);

        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingVisit.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, active.StatusCode);
        var history = await client.GetFromJsonAsync<AssignmentHistoryResponse>($"/api/visits/{visitId}/assignments/history", Cancellation);
        Assert.Equal((assignment.AssignmentId, "Cancelled"), (Assert.Single(history!.Assignments).AssignmentId, history.Assignments[0].Status));

        // Reservations were released: the technician can take another visit at the same time.
        (await AssignAsync(client, await CreateVisitAsync(client), Request(technicianId))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Get_endpoints_return_404_for_a_missing_visit_or_assignment()
    {
        using var client = factory.CreateClient();
        var visitId = await CreateVisitAsync(client);

        using var noAssignment = await client.GetAsync($"/api/visits/{visitId}/assignment", Cancellation);
        using var missingVisit = await client.GetAsync($"/api/visits/{Guid.NewGuid()}/assignment", Cancellation);
        using var missingHistory = await client.GetAsync($"/api/visits/{Guid.NewGuid()}/assignments/history", Cancellation);
        using var reassignNothing = await client.PostAsJsonAsync($"/api/visits/{visitId}/assignment/reassign",
            new ReassignAssignmentRequest(await CreateWorkingTechnicianAsync(client), null, null, null, null, null), Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, noAssignment.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingVisit.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingHistory.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, reassignNothing.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<AssignmentHistoryResponse>($"/api/visits/{visitId}/assignments/history", Cancellation))!.Assignments);
    }
}
