using System.Net;
using System.Net.Http.Json;
using CrewCall.Contracts.Absences;
using CrewCall.Contracts.Equipment;
using CrewCall.Contracts.Scheduling;
using CrewCall.Contracts.Skills;
using CrewCall.Contracts.Vehicles;
using CrewCall.Contracts.WorkingHours;
using CrewCall.Scheduling.Ports;
using CrewCall.Scheduling.Reservations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Api.Tests;

/// <summary>
/// The scheduling check end to end: real Workforce data (working hours, absences, skills) reached through the
/// composition-root adapters, real Resources, and reservations in PostgreSQL.
/// </summary>
public sealed class SchedulingCheckEndpointsTests(CrewCallApiFactory factory)
{
    // Monday 2026-07-06, Europe/Warsaw summer time (UTC+2): working hours 08:00–16:00 local are 06:00Z–14:00Z.
    private static readonly DateTimeOffset _visitStart = new(2026, 7, 6, 8, 0, 0, TimeSpan.Zero); // 10:00 local
    private static readonly DateTimeOffset _visitEnd = new(2026, 7, 6, 9, 0, 0, TimeSpan.Zero);   // 11:00 local

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object request)
    {
        using var response = await client.PostAsJsonAsync(url, request, Cancellation);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Cancellation))!;
    }

    /// <summary>An active Warsaw technician working Monday 08:00–16:00, with the given skill codes.</summary>
    private static async Task<Guid> CreateWorkingTechnicianAsync(HttpClient client, params string[] skillCodes)
    {
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);
        await PostAsync<WorkingHoursResponse>(client, $"/api/technicians/{technicianId}/working-hours",
            new CreateWorkingHoursRequest("Monday", "08:00", "16:00"));

        foreach (var code in skillCodes)
        {
            var skill = await PostAsync<SkillResponse>(client, "/api/skills", new CreateSkillRequest(code, code, null));
            (await client.PostAsync($"/api/technicians/{technicianId}/skills/{skill.Id}", null, Cancellation)).EnsureSuccessStatusCode();
        }

        return technicianId;
    }

    private static string UniqueCode(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20].ToUpperInvariant();

    private static async Task<Guid> CreateVehicleAsync(HttpClient client) =>
        (await PostAsync<VehicleResponse>(client, "/api/vehicles", new CreateVehicleRequest(UniqueCode("PO"), "Van", "Van", null))).Id;

    private static async Task<Guid> CreateEquipmentAsync(HttpClient client) =>
        (await PostAsync<EquipmentResponse>(client, "/api/equipment", new CreateEquipmentRequest("Fiber tester", UniqueCode("EQ"), null))).Id;

    private async Task ReserveAsync(ResourceType type, Guid resourceId, DateTimeOffset start, DateTimeOffset end, Guid? visitId = null)
    {
        // Reservations are fixtures here: no API writes them yet (Assignment will, in a later sprint).
        await using var scope = factory.Services.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<ResourceReservationService>()
            .CreateAsync(new CreateReservation(type.ToString(), resourceId, visitId, start, end), Cancellation);
        Assert.IsType<CreateReservationOutcome.Created>(outcome);
    }

    private static SchedulingCheckRequest Request(
        Guid technicianId,
        Guid? vehicleId = null,
        Guid[]? equipmentIds = null,
        string[]? skills = null,
        int? before = null,
        int? after = null,
        Guid? visitId = null) =>
        new(technicianId, vehicleId, equipmentIds, visitId, _visitStart, _visitEnd, skills, before, after);

    private static async Task<SchedulingCheckResponse> CheckAsync(HttpClient client, SchedulingCheckRequest request)
    {
        using var response = await client.PostAsJsonAsync("/api/scheduling/check", request, Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SchedulingCheckResponse>(Cancellation))!;
    }

    [Fact]
    public async Task A_feasible_check_returns_200_with_no_reasons()
    {
        using var client = factory.CreateClient();
        var code = UniqueCode("ELEC");
        var technicianId = await CreateWorkingTechnicianAsync(client, code);
        var vehicleId = await CreateVehicleAsync(client);
        var equipmentId = await CreateEquipmentAsync(client);

        var result = await CheckAsync(client, Request(technicianId, vehicleId, [equipmentId], [code.ToLowerInvariant()], 15, 30));

        Assert.True(result.IsFeasible);
        Assert.Empty(result.Reasons);
        Assert.Equal(technicianId, result.TechnicianId);
        Assert.Equal(_visitStart.AddMinutes(-15), result.EffectiveStart);
        Assert.Equal(_visitEnd.AddMinutes(30), result.EffectiveEnd);
    }

    [Fact]
    public async Task An_infeasible_check_still_returns_200_with_typed_reasons()
    {
        using var client = factory.CreateClient();
        var technicianId = await CreateWorkingTechnicianAsync(client);
        var vehicleId = await CreateVehicleAsync(client);
        var firstAsset = await CreateEquipmentAsync(client);
        var secondAsset = await CreateEquipmentAsync(client);
        await ReserveAsync(ResourceType.Technician, technicianId, _visitStart, _visitEnd);
        await ReserveAsync(ResourceType.Vehicle, vehicleId, _visitStart.AddMinutes(30), _visitEnd.AddHours(1));
        await ReserveAsync(ResourceType.Equipment, firstAsset, _visitStart, _visitEnd);
        await ReserveAsync(ResourceType.Equipment, secondAsset, _visitEnd.AddMinutes(10), _visitEnd.AddHours(1));

        var result = await CheckAsync(client, Request(technicianId, vehicleId, [firstAsset, secondAsset], ["NO-SUCH-SKILL"], after: 20));

        Assert.False(result.IsFeasible);
        Assert.Equal(
            ["MissingRequiredSkills", "TechnicianReservationConflict", "VehicleReservationConflict", "EquipmentReservationConflict", "TravelBufferConflict"],
            result.Reasons.Select(reason => reason.Code));
        Assert.Equal(firstAsset, result.Reasons.Single(reason => reason.Code == "EquipmentReservationConflict").RelatedResourceId);
        var buffer = result.Reasons.Single(reason => reason.Code == "TravelBufferConflict");
        Assert.Equal(secondAsset, buffer.RelatedResourceId);
        Assert.Equal("Equipment", buffer.ResourceType);
        Assert.Equal(["NO-SUCH-SKILL"], result.Reasons[0].Details);
    }

    [Fact]
    public async Task Workforce_absence_and_working_hours_are_consumed()
    {
        using var client = factory.CreateClient();
        var technicianId = await CreateWorkingTechnicianAsync(client);
        await PostAsync<AbsenceResponse>(client, $"/api/technicians/{technicianId}/absences",
            new CreateAbsenceRequest(_visitStart.AddMinutes(-30), _visitStart.AddMinutes(30), "SickLeave", null));

        var absent = await CheckAsync(client, Request(technicianId));
        var outsideHours = await CheckAsync(client, Request(technicianId) with
        {
            Start = new DateTimeOffset(2026, 7, 6, 15, 0, 0, TimeSpan.Zero), // 17:00 local
            End = new DateTimeOffset(2026, 7, 6, 16, 0, 0, TimeSpan.Zero)
        });

        var absence = Assert.Single(absent.Reasons);
        Assert.Equal("TechnicianUnavailable", absence.Code);
        Assert.Equal(["Absence", "SickLeave"], absence.Details);
        Assert.Equal(["OutsideWorkingHours"], Assert.Single(outsideHours.Reasons).Details);
    }

    [Fact]
    public async Task An_inactive_technician_is_reported_as_inactive()
    {
        using var client = factory.CreateClient();
        var technicianId = await ApiTestData.CreateTechnicianAsync(client, isActive: false);

        var result = await CheckAsync(client, Request(technicianId));

        Assert.Equal("TechnicianInactive", Assert.Single(result.Reasons).Code);
    }

    [Fact]
    public async Task The_visits_own_reservations_do_not_conflict()
    {
        using var client = factory.CreateClient();
        var technicianId = await CreateWorkingTechnicianAsync(client);
        var visitId = Guid.NewGuid();
        await ReserveAsync(ResourceType.Technician, technicianId, _visitStart, _visitEnd, visitId);

        Assert.True((await CheckAsync(client, Request(technicianId, visitId: visitId))).IsFeasible);
        Assert.False((await CheckAsync(client, Request(technicianId))).IsFeasible);
    }

    [Fact]
    public async Task Missing_resources_return_404()
    {
        using var client = factory.CreateClient();
        var technicianId = await CreateWorkingTechnicianAsync(client);

        using var missingTechnician = await client.PostAsJsonAsync("/api/scheduling/check", Request(Guid.NewGuid()), Cancellation);
        using var missingVehicle = await client.PostAsJsonAsync("/api/scheduling/check", Request(technicianId, Guid.NewGuid()), Cancellation);
        using var missingEquipment = await client.PostAsJsonAsync(
            "/api/scheduling/check", Request(technicianId, equipmentIds: [Guid.NewGuid()]), Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, missingTechnician.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingVehicle.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingEquipment.StatusCode);
    }

    [Fact]
    public async Task Invalid_requests_return_400()
    {
        using var client = factory.CreateClient();
        var technicianId = await CreateWorkingTechnicianAsync(client);

        using var negativeBuffer = await client.PostAsJsonAsync("/api/scheduling/check", Request(technicianId, before: -5), Cancellation);
        using var reversed = await client.PostAsJsonAsync(
            "/api/scheduling/check", Request(technicianId) with { Start = _visitEnd, End = _visitStart }, Cancellation);
        using var noTechnician = await client.PostAsJsonAsync(
            "/api/scheduling/check", Request(technicianId) with { TechnicianId = null }, Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, negativeBuffer.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noTechnician.StatusCode);
    }

    // --- The Workforce adapter itself (composition root) ---

    [Fact]
    public async Task The_workforce_adapter_reports_active_skills_and_Workforce_availability()
    {
        using var client = factory.CreateClient();
        var code = UniqueCode("HVAC");
        var technicianId = await CreateWorkingTechnicianAsync(client, code);

        await using var scope = factory.Services.CreateAsyncScope();
        var source = scope.ServiceProvider.GetRequiredService<ITechnicianSchedulingSource>();
        var available = await source.GetProfileAsync(technicianId, _visitStart, _visitEnd, Cancellation);
        var outside = await source.GetProfileAsync(technicianId, _visitStart.AddHours(10), _visitEnd.AddHours(10), Cancellation);
        var missing = await source.GetProfileAsync(Guid.NewGuid(), _visitStart, _visitEnd, Cancellation);

        Assert.NotNull(available);
        Assert.True(available.IsActive);
        Assert.Equal([code], available.SkillCodes);
        Assert.Equal(TechnicianAvailabilityState.Available, available.Availability);
        Assert.Equal(TechnicianAvailabilityState.OutsideWorkingHours, outside!.Availability);
        Assert.Null(missing);
    }
}
