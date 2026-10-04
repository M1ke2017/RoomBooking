using CrewCall.Scheduling.Checks;
using CrewCall.Scheduling.Ports;
using CrewCall.Scheduling.Reservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Scheduling.Tests;

public sealed class SchedulingCheckServiceTests(SchedulingDatabase database)
{
    // Visit under check: 10:00–11:00 UTC on 2026-07-06.
    private static readonly DateTimeOffset _day = new(2026, 7, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _visitStart = _day.AddHours(10);
    private static readonly DateTimeOffset _visitEnd = _day.AddHours(11);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private async Task<Guid> ReserveAsync(ResourceType type, Guid resourceId, DateTimeOffset start, DateTimeOffset end, Guid? visitId = null)
    {
        await using var scope = database.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<ResourceReservationService>()
            .CreateAsync(new CreateReservation(type.ToString(), resourceId, visitId, start, end), Cancellation);
        return Assert.IsType<CreateReservationOutcome.Created>(outcome).Reservation.Id;
    }

    private async Task<SchedulingCheckOutcome> CheckAsync(
        Guid technicianId,
        Guid? vehicleId = null,
        Guid[]? equipmentIds = null,
        string[]? skills = null,
        int? before = null,
        int? after = null,
        Guid? visitId = null,
        DateTimeOffset? start = null,
        DateTimeOffset? end = null)
    {
        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SchedulingCheckService>().CheckAsync(
            new SchedulingCheck(visitId, technicianId, vehicleId, equipmentIds, start ?? _visitStart, end ?? _visitEnd, skills, before, after),
            Cancellation);
    }

    private static SchedulingCheckResult Checked(SchedulingCheckOutcome outcome) =>
        Assert.IsType<SchedulingCheckOutcome.Checked>(outcome).Result;

    private static SchedulingConflictCode[] Codes(SchedulingCheckResult result) => result.Reasons.Select(reason => reason.Code).ToArray();

    [Fact]
    public async Task A_fully_feasible_request_has_no_reasons()
    {
        var technicianId = database.Technicians.Add(skillCodes: ["ELECTRICAL"]);
        var vehicleId = database.Resources.AddVehicle();
        var equipmentId = database.Resources.AddEquipment();

        var result = Checked(await CheckAsync(technicianId, vehicleId, [equipmentId], ["electrical"]));

        Assert.True(result.IsFeasible);
        Assert.Empty(result.Reasons);
        Assert.Equal(_visitStart, result.EffectiveStart);
        Assert.Equal(_visitEnd, result.EffectiveEnd);
    }

    [Fact]
    public async Task An_inactive_technician_is_reported_once()
    {
        var technicianId = database.Technicians.Add(isActive: false, availability: TechnicianAvailabilityState.Inactive);

        var result = Checked(await CheckAsync(technicianId));

        Assert.False(result.IsFeasible);
        Assert.Equal([SchedulingConflictCode.TechnicianInactive], Codes(result));
    }

    [Fact]
    public async Task A_missing_skill_is_reported_with_its_code()
    {
        var technicianId = database.Technicians.Add(skillCodes: ["ELECTRICAL"]);

        var result = Checked(await CheckAsync(technicianId, skills: ["Electrical", "Fiber"]));

        var reason = Assert.Single(result.Reasons);
        Assert.Equal(SchedulingConflictCode.MissingRequiredSkills, reason.Code);
        Assert.Equal(["FIBER"], reason.Details);
        Assert.Equal(technicianId, reason.RelatedResourceId);
    }

    [Theory]
    [InlineData(TechnicianAvailabilityState.Absence, "Absence")]
    [InlineData(TechnicianAvailabilityState.Holiday, "Holiday")]
    [InlineData(TechnicianAvailabilityState.OutsideWorkingHours, "OutsideWorkingHours")]
    public async Task Workforce_unavailability_is_reported_with_its_reason(TechnicianAvailabilityState availability, string reasonName)
    {
        var technicianId = database.Technicians.Add(availability: availability, detail: "from Workforce");

        var result = Checked(await CheckAsync(technicianId));

        var reason = Assert.Single(result.Reasons);
        Assert.Equal(SchedulingConflictCode.TechnicianUnavailable, reason.Code);
        Assert.Equal([reasonName, "from Workforce"], reason.Details);
    }

    [Fact]
    public async Task A_technician_reservation_overlapping_the_visit_is_a_conflict()
    {
        var technicianId = database.Technicians.Add();
        var reservationId = await ReserveAsync(ResourceType.Technician, technicianId, _day.AddHours(9), _day.AddHours(10).AddMinutes(30));

        var result = Checked(await CheckAsync(technicianId));

        var reason = Assert.Single(result.Reasons);
        Assert.Equal(SchedulingConflictCode.TechnicianReservationConflict, reason.Code);
        Assert.Equal(technicianId, reason.RelatedResourceId);
        Assert.Equal(reservationId, reason.ReservationId);
        Assert.Equal(ResourceType.Technician, reason.ResourceType);
    }

    [Fact]
    public async Task A_vehicle_reservation_overlapping_the_visit_is_a_conflict()
    {
        var technicianId = database.Technicians.Add();
        var vehicleId = database.Resources.AddVehicle();
        await ReserveAsync(ResourceType.Vehicle, vehicleId, _visitStart, _visitEnd);

        var result = Checked(await CheckAsync(technicianId, vehicleId));

        var reason = Assert.Single(result.Reasons);
        Assert.Equal(SchedulingConflictCode.VehicleReservationConflict, reason.Code);
        Assert.Equal(vehicleId, reason.RelatedResourceId);
    }

    [Fact]
    public async Task Each_conflicting_equipment_asset_is_reported_by_id()
    {
        var technicianId = database.Technicians.Add();
        var free = database.Resources.AddEquipment();
        var drill = database.Resources.AddEquipment();
        var tester = database.Resources.AddEquipment();
        await ReserveAsync(ResourceType.Equipment, drill, _day.AddHours(10).AddMinutes(30), _day.AddHours(12));
        await ReserveAsync(ResourceType.Equipment, tester, _day.AddHours(9), _day.AddHours(10).AddMinutes(15));

        var result = Checked(await CheckAsync(technicianId, equipmentIds: [free, drill, tester]));

        Assert.All(result.Reasons, reason => Assert.Equal(SchedulingConflictCode.EquipmentReservationConflict, reason.Code));
        Assert.Equal(
            new[] { drill, tester }.Order(),
            result.Reasons.Select(reason => reason.RelatedResourceId!.Value).Order());
    }

    [Fact]
    public async Task Every_problem_is_reported_together_without_short_circuit()
    {
        var technicianId = database.Technicians.Add(availability: TechnicianAvailabilityState.Absence, skillCodes: ["ELECTRICAL"]);
        var vehicleId = database.Resources.AddVehicle();
        var equipmentId = database.Resources.AddEquipment();
        await ReserveAsync(ResourceType.Technician, technicianId, _visitStart, _visitEnd);
        await ReserveAsync(ResourceType.Vehicle, vehicleId, _visitStart, _visitEnd);
        await ReserveAsync(ResourceType.Equipment, equipmentId, _visitEnd, _visitEnd.AddHours(1));

        var result = Checked(await CheckAsync(technicianId, vehicleId, [equipmentId], ["Fiber"], after: 30));

        Assert.False(result.IsFeasible);
        Assert.Equal(
            [
                SchedulingConflictCode.MissingRequiredSkills,
                SchedulingConflictCode.TechnicianUnavailable,
                SchedulingConflictCode.TechnicianReservationConflict,
                SchedulingConflictCode.VehicleReservationConflict,
                SchedulingConflictCode.TravelBufferConflict
            ],
            Codes(result));
    }

    [Fact]
    public async Task A_reservation_inside_the_after_buffer_is_a_travel_buffer_conflict()
    {
        // Visit 10:00–11:00 with 30 minutes after; an existing reservation at 11:15–12:00.
        var technicianId = database.Technicians.Add();
        await ReserveAsync(ResourceType.Technician, technicianId, _day.AddHours(11).AddMinutes(15), _day.AddHours(12));

        var result = Checked(await CheckAsync(technicianId, after: 30));

        var reason = Assert.Single(result.Reasons);
        Assert.Equal(SchedulingConflictCode.TravelBufferConflict, reason.Code);
        Assert.Equal(technicianId, reason.RelatedResourceId);
        Assert.Equal(_visitEnd.AddMinutes(30), result.EffectiveEnd);
    }

    [Fact]
    public async Task A_reservation_inside_the_before_buffer_is_a_travel_buffer_conflict()
    {
        var technicianId = database.Technicians.Add();
        var vehicleId = database.Resources.AddVehicle();
        await ReserveAsync(ResourceType.Vehicle, vehicleId, _day.AddHours(9), _day.AddHours(9).AddMinutes(50));

        var result = Checked(await CheckAsync(technicianId, vehicleId, before: 15));

        var reason = Assert.Single(result.Reasons);
        Assert.Equal(SchedulingConflictCode.TravelBufferConflict, reason.Code);
        Assert.Equal(ResourceType.Vehicle, reason.ResourceType);
        Assert.Equal(_visitStart.AddMinutes(-15), result.EffectiveStart);
    }

    [Fact]
    public async Task Without_a_buffer_an_adjacent_reservation_is_not_a_conflict()
    {
        var technicianId = database.Technicians.Add();
        await ReserveAsync(ResourceType.Technician, technicianId, _day.AddHours(11).AddMinutes(15), _day.AddHours(12));
        await ReserveAsync(ResourceType.Technician, technicianId, _day.AddHours(9), _visitStart);

        Assert.True(Checked(await CheckAsync(technicianId)).IsFeasible);
        Assert.True(Checked(await CheckAsync(technicianId, before: 0, after: 0)).IsFeasible);
    }

    [Fact]
    public async Task A_reservation_touching_the_end_of_the_buffer_is_allowed()
    {
        // Visit 10:00–11:00 with 30 minutes after; an existing reservation at 11:30–12:00 touches the buffered end.
        var technicianId = database.Technicians.Add();
        await ReserveAsync(ResourceType.Technician, technicianId, _day.AddHours(11).AddMinutes(30), _day.AddHours(12));

        Assert.True(Checked(await CheckAsync(technicianId, after: 30)).IsFeasible);
    }

    [Fact]
    public async Task The_visits_own_reservations_are_ignored()
    {
        var technicianId = database.Technicians.Add();
        var vehicleId = database.Resources.AddVehicle();
        var visitId = Guid.NewGuid();
        await ReserveAsync(ResourceType.Technician, technicianId, _visitStart, _visitEnd, visitId);
        await ReserveAsync(ResourceType.Vehicle, vehicleId, _visitStart, _visitEnd, visitId);

        Assert.True(Checked(await CheckAsync(technicianId, vehicleId, visitId: visitId)).IsFeasible);
        Assert.Equal(
            [SchedulingConflictCode.TechnicianReservationConflict, SchedulingConflictCode.VehicleReservationConflict],
            Codes(Checked(await CheckAsync(technicianId, vehicleId, visitId: Guid.NewGuid()))));
    }

    [Fact]
    public async Task Without_vehicle_or_equipment_those_checks_are_skipped()
    {
        var technicianId = database.Technicians.Add();
        // Another technician's vehicle and equipment are busy; this request names none of them.
        await ReserveAsync(ResourceType.Vehicle, database.Resources.AddVehicle(), _visitStart, _visitEnd);
        await ReserveAsync(ResourceType.Equipment, database.Resources.AddEquipment(), _visitStart, _visitEnd);

        var result = Checked(await CheckAsync(technicianId, vehicleId: null, equipmentIds: null));

        Assert.True(result.IsFeasible);
        Assert.Null(result.VehicleId);
        Assert.Empty(result.EquipmentIds);
    }

    [Fact]
    public async Task The_check_writes_nothing()
    {
        var technicianId = database.Technicians.Add();
        var vehicleId = database.Resources.AddVehicle();

        Assert.True(Checked(await CheckAsync(technicianId, vehicleId)).IsFeasible);

        await using var scope = database.CreateScope();
        var reservations = scope.ServiceProvider.GetRequiredService<ISchedulingDbContext>().ResourceReservations;
        Assert.False(await reservations.AnyAsync(
            reservation => reservation.ResourceId == technicianId || reservation.ResourceId == vehicleId, Cancellation));
    }

    [Fact]
    public async Task Missing_resources_are_all_reported()
    {
        var missingTechnician = Guid.NewGuid();
        var missingVehicle = Guid.NewGuid();
        var missingEquipment = Guid.NewGuid();
        var existingEquipment = database.Resources.AddEquipment();

        var outcome = await CheckAsync(missingTechnician, missingVehicle, [existingEquipment, missingEquipment]);

        var notFound = Assert.IsType<SchedulingCheckOutcome.ResourcesNotFound>(outcome);
        Assert.Equal(
            [(ResourceType.Technician, missingTechnician), (ResourceType.Vehicle, missingVehicle), (ResourceType.Equipment, missingEquipment)],
            notFound.Missing);
    }

    [Theory]
    [InlineData(-1, 0, "travelBufferBeforeMinutes")]
    [InlineData(0, -5, "travelBufferAfterMinutes")]
    [InlineData(481, 0, "travelBufferBeforeMinutes")]
    public async Task Buffers_outside_0_to_480_minutes_are_invalid(int before, int after, string field)
    {
        var invalid = Assert.IsType<SchedulingCheckOutcome.Invalid>(
            await CheckAsync(database.Technicians.Add(), before: before, after: after));

        Assert.Equal([field], invalid.Errors.Keys);
    }

    [Fact]
    public async Task Invalid_requests_report_each_field()
    {
        await using var scope = database.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SchedulingCheckService>();

        var missing = Assert.IsType<SchedulingCheckOutcome.Invalid>(await service.CheckAsync(
            new SchedulingCheck(Guid.Empty, null, Guid.Empty, [Guid.Empty], null, null, null, null, null), Cancellation));
        Assert.Equal(["end", "equipmentIds", "start", "technicianId", "vehicleId", "visitId"], missing.Errors.Keys.Order());

        var reversed = Assert.IsType<SchedulingCheckOutcome.Invalid>(await CheckAsync(Guid.NewGuid(), start: _visitEnd, end: _visitStart));
        Assert.Equal(["end"], reversed.Errors.Keys);

        var tooLong = Assert.IsType<SchedulingCheckOutcome.Invalid>(await CheckAsync(Guid.NewGuid(), end: _visitStart.AddDays(32)));
        Assert.Equal(["end"], tooLong.Errors.Keys);
    }

    [Fact]
    public async Task A_buffer_past_the_end_of_time_is_invalid_not_an_exception()
    {
        var invalid = Assert.IsType<SchedulingCheckOutcome.Invalid>(await CheckAsync(
            database.Technicians.Add(),
            after: 30,
            start: DateTimeOffset.MaxValue.AddMinutes(-60),
            end: DateTimeOffset.MaxValue.AddMinutes(-10)));

        Assert.Equal(["travelBuffer"], invalid.Errors.Keys);
    }

    [Fact]
    public async Task Duplicate_equipment_ids_are_checked_once()
    {
        var technicianId = database.Technicians.Add();
        var drill = database.Resources.AddEquipment();
        await ReserveAsync(ResourceType.Equipment, drill, _visitStart, _visitEnd);

        var result = Checked(await CheckAsync(technicianId, equipmentIds: [drill, drill]));

        Assert.Equal([drill], result.EquipmentIds);
        Assert.Single(result.Reasons);
    }
}
