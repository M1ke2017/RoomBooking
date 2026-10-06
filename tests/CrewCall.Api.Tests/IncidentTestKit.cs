using System.Net.Http.Json;
using System.Text.Json;
using CrewCall.Contracts.Equipment;
using CrewCall.Contracts.Incidents;
using CrewCall.Contracts.Operations;
using CrewCall.Contracts.Scheduling;
using CrewCall.Contracts.Skills;
using CrewCall.Contracts.Vehicles;
using CrewCall.Contracts.Visits;
using CrewCall.Contracts.WorkingHours;
using CrewCall.Contracts.WorkOrders;
using CrewCall.Persistence;
using CrewCall.Scheduling.Reservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Api.Tests;

/// <summary>
/// Shared setup of the incident workflow tests. Every test works on its own UTC day (from 2034 on), with its own
/// customer and its own technicians, and analyses only its own technicians (candidate subset), so the data of other
/// tests in the shared database never takes part.
/// </summary>
internal static class IncidentTestKit
{
    private static int _daySequence;

    public static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public static DateTimeOffset NewDay() =>
        new DateTimeOffset(2034, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(Interlocked.Increment(ref _daySequence));

    public static async Task<T> PostAsync<T>(HttpClient client, string url, object? request)
    {
        using var response = await client.PostAsJsonAsync(url, request, Cancellation);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Cancellation))!;
    }

    public static async Task<T> GetAsync<T>(HttpClient client, string url) =>
        (await client.GetFromJsonAsync<T>(url, Cancellation))!;

    public static string UniqueCode(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20].ToUpperInvariant();

    /// <summary>Records compare arrays by reference; compare what the API returns instead.</summary>
    public static void AssertSameJson<T>(T expected, T actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

    public static Task<VehicleResponse> CreateVehicleAsync(HttpClient client) =>
        PostAsync<VehicleResponse>(client, "/api/vehicles", new CreateVehicleRequest(UniqueCode("PO"), "Van", null, null));

    public static Task<EquipmentResponse> CreateEquipmentAsync(HttpClient client) =>
        PostAsync<EquipmentResponse>(client, "/api/equipment", new CreateEquipmentRequest("Generator", UniqueCode("EQ"), null));

    public static async Task<Guid> CreateSkillAsync(HttpClient client, string code) =>
        (await PostAsync<SkillResponse>(client, "/api/skills", new CreateSkillRequest(code, code, null))).Id;

    /// <summary>A Warsaw technician working every day 00:00–24:00, with the given skills.</summary>
    public static async Task<Guid> CreateTechnicianAsync(HttpClient client, bool isActive = true, params Guid[] skillIds)
    {
        var technicianId = await ApiTestData.CreateTechnicianAsync(client, isActive: isActive);
        foreach (var day in Enum.GetNames<DayOfWeek>())
        {
            await PostAsync<WorkingHoursResponse>(client, $"/api/technicians/{technicianId}/working-hours", new CreateWorkingHoursRequest(day, "00:00", "24:00"));
        }

        foreach (var skillId in skillIds)
        {
            (await client.PostAsync($"/api/technicians/{technicianId}/skills/{skillId}", null, Cancellation)).EnsureSuccessStatusCode();
        }

        return technicianId;
    }

    public static CreateIncidentRequest IncidentRequest(
        Guid customerId, Guid siteId, DateTimeOffset start, DateTimeOffset end, string priority = "Urgent", params string[] skills) =>
        new(customerId, siteId, "Power outage", "Main switchboard down", priority, start, end, skills);

    /// <summary>A new incident (its own customer and site) for [start, end).</summary>
    public static async Task<IncidentResponse> CreateIncidentAsync(
        HttpClient client, DateTimeOffset start, DateTimeOffset end, string priority = "Urgent", params string[] skills)
    {
        var customerId = await ApiTestData.CreateCustomerAsync(client);
        var siteId = await ApiTestData.CreateSiteAsync(client, customerId);
        return await PostAsync<IncidentResponse>(client, "/api/incidents", IncidentRequest(customerId, siteId, start, end, priority, skills));
    }

    public static Task<IncidentAnalysisResponse> AnalyzeAsync(HttpClient client, Guid incidentId, params Guid[] candidates) =>
        PostAsync<IncidentAnalysisResponse>(client, $"/api/incidents/{incidentId}/analyze", new AnalyzeIncidentRequest(candidates, null));

    /// <summary>A new incident 10:00–12:00 on <paramref name="day"/>, analysed (ReadyForDispatch) for the candidates.</summary>
    public static async Task<IncidentResponse> ReadyIncidentAsync(
        HttpClient client, DateTimeOffset day, Guid[] candidates, string priority = "Urgent", params string[] skills)
    {
        var incident = await CreateIncidentAsync(client, day.AddHours(10), day.AddHours(12), priority, skills);
        await AnalyzeAsync(client, incident.Id, candidates);
        return incident;
    }

    public static Task<HttpResponseMessage> DispatchAsync(HttpClient client, Guid incidentId, DispatchIncidentRequest request) =>
        client.PostAsJsonAsync($"/api/incidents/{incidentId}/dispatch", request, Cancellation);

    public static DispatchIncidentRequest Dispatch(Guid technicianId, Guid? vehicleId = null, Guid[]? equipmentIds = null) =>
        new(technicianId, vehicleId, equipmentIds, null, null);

    /// <summary>Existing work: a work order with a visit over [start, end), assigned to the technician.</summary>
    public static async Task<(WorkOrderResponse WorkOrder, VisitResponse Visit, AssignmentResponse Assignment)> ExistingWorkAsync(
        HttpClient client, Guid technicianId, DateTimeOffset start, DateTimeOffset end, Guid? vehicleId = null)
    {
        var workOrder = await ApiTestData.CreateWorkOrderAsync(client);
        var visit = await PostAsync<VisitResponse>(client, $"/api/work-orders/{workOrder.Id}/visits", new CreateVisitRequest(start, end, null));
        var assignment = await PostAsync<AssignmentResponse>(
            client, $"/api/visits/{visit.Id}/assignment", new CreateAssignmentRequest(technicianId, vehicleId, null, null, null, null));
        return (workOrder, visit, assignment);
    }

    public static Task<OperationalEventResponse[]> EventsAsync(HttpClient client, string aggregateType, Guid aggregateId) =>
        GetAsync<OperationalEventResponse[]>(client, $"/api/operations/events/{aggregateType}/{aggregateId}");

    /// <summary>What exists in the database for a customer and for resources: proof that nothing partial was written.</summary>
    public static async Task<DatabaseState> StateAsync(CrewCallApiFactory factory, Guid customerId, params Guid[] resourceIds)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CrewCallDbContext>();

        var workOrderIds = await db.WorkOrders.Where(w => w.CustomerId == customerId).Select(w => w.Id).ToListAsync(Cancellation);
        var visitIds = await db.Visits.Where(v => workOrderIds.Contains(v.WorkOrderId)).Select(v => v.Id).ToListAsync(Cancellation);
        var assignments = await db.Assignments.CountAsync(a => visitIds.Contains(a.VisitId) || resourceIds.Contains(a.TechnicianId), Cancellation);
        var reservations = await db.ResourceReservations.CountAsync(r => resourceIds.Contains(r.ResourceId), Cancellation);
        var workOrderEvents = await db.OperationalEvents.CountAsync(
            e => e.EventType == "WorkOrderCreated" && EF.Functions.JsonContains(e.PayloadJson, $"{{\"customerId\":\"{customerId}\"}}"),
            Cancellation);

        return new DatabaseState(workOrderIds.Count, visitIds.Count, assignments, reservations, workOrderEvents);
    }

    public static async Task<int> ReservationCountAsync(CrewCallApiFactory factory, Guid resourceId, ResourceType type)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CrewCallDbContext>();
        return await db.ResourceReservations.CountAsync(r => r.ResourceId == resourceId && r.ResourceType == type, Cancellation);
    }

    public static async Task<string[]> IncidentEventTypesAsync(CrewCallApiFactory factory, Guid incidentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CrewCallDbContext>();
        return await db.OperationalEvents
            .Where(e => e.AggregateType == "incident" && e.AggregateId == incidentId)
            .OrderBy(e => e.Sequence)
            .Select(e => e.EventType)
            .ToArrayAsync(Cancellation);
    }
}

/// <param name="WorkOrders">Work orders of the customer.</param>
/// <param name="Visits">Visits of those work orders.</param>
/// <param name="Assignments">Assignments of those visits or of the technicians.</param>
/// <param name="Reservations">Reservations of the resources.</param>
/// <param name="WorkOrderCreatedEvents">WorkOrderCreated events naming the customer.</param>
internal sealed record DatabaseState(int WorkOrders, int Visits, int Assignments, int Reservations, int WorkOrderCreatedEvents);
