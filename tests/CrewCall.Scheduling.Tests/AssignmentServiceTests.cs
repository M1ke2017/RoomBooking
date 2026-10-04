using CrewCall.Persistence;
using CrewCall.Scheduling.Assignments;
using CrewCall.Scheduling.Checks;
using CrewCall.Scheduling.Ports;
using CrewCall.Scheduling.Reservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Scheduling.Tests;

public sealed class AssignmentServiceTests(SchedulingDatabase database)
{
    // Visits under test: 10:00–11:00 UTC on 2026-07-06.
    private static readonly DateTimeOffset _start = new(2026, 7, 6, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _end = _start.AddHours(1);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private Guid NewVisit(VisitState state = VisitState.Planned) => database.Visits.Add(_start, _end, state);

    private async Task<AssignOutcome> AssignAsync(
        Guid visitId, Guid technicianId, Guid? vehicleId = null, Guid[]? equipmentIds = null,
        string[]? skills = null, int? before = null, int? after = null)
    {
        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AssignmentService>().CreateAsync(
            new CreateAssignment(visitId, technicianId, vehicleId, equipmentIds, skills, before, after), Cancellation);
    }

    private async Task<AssignOutcome> ReassignAsync(
        Guid visitId, Guid technicianId, Guid? vehicleId = null, Guid[]? equipmentIds = null, int? before = null, int? after = null)
    {
        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AssignmentService>().ReassignAsync(
            new ReassignVisit(visitId, technicianId, vehicleId, equipmentIds, null, before, after), Cancellation);
    }

    private async Task<CancelAssignmentOutcome> CancelAsync(Guid visitId)
    {
        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AssignmentService>().CancelAsync(visitId, Cancellation);
    }

    private async Task<List<ResourceReservation>> ReservationsOfVisitAsync(Guid visitId)
    {
        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().ResourceReservations
            .AsNoTracking()
            .Where(reservation => reservation.VisitId == visitId)
            .ToListAsync(Cancellation);
    }

    private async Task<List<Assignment>> AssignmentsOfVisitAsync(Guid visitId)
    {
        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().Assignments
            .AsNoTracking()
            .Include(assignment => assignment.Equipment)
            .Where(assignment => assignment.VisitId == visitId)
            .OrderBy(assignment => assignment.CreatedAtUtc)
            .ToListAsync(Cancellation);
    }

    private async Task<List<string>> EventTypesAsync(Guid aggregateId)
    {
        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().OperationalEvents
            .AsNoTracking()
            .Where(e => e.AggregateType == AssignmentEvents.AssignmentAggregate && e.AggregateId == aggregateId)
            .OrderBy(e => e.Sequence)
            .Select(e => e.EventType)
            .ToListAsync(Cancellation);
    }

    private async Task<int> EventsMentioningAsync(Guid visitId)
    {
        await using var scope = database.CreateScope();
        var visitFilter = $"{{\"visitId\":\"{visitId}\"}}";
        return await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().OperationalEvents
            .AsNoTracking()
            .CountAsync(e => e.AggregateType == AssignmentEvents.AssignmentAggregate && EF.Functions.JsonContains(e.PayloadJson, visitFilter), Cancellation);
    }

    private async Task ReserveForOtherVisitAsync(ResourceType type, Guid resourceId, DateTimeOffset start, DateTimeOffset end)
    {
        await using var scope = database.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<ResourceReservationService>()
            .CreateAsync(new CreateReservation(type.ToString(), resourceId, Guid.NewGuid(), start, end), Cancellation);
        Assert.IsType<CreateReservationOutcome.Created>(outcome);
    }

    private static Assignment Assigned(AssignOutcome outcome) => Assert.IsType<AssignOutcome.Assigned>(outcome).Assignment;

    // ---------------------------------------------------------------- creation

    [Fact]
    public async Task Create_writes_the_assignment_its_reservations_and_its_event()
    {
        var visitId = NewVisit();
        var technicianId = database.Technicians.Add(skillCodes: ["ELECTRICAL"]);
        var vehicleId = database.Resources.AddVehicle();
        var drill = database.Resources.AddEquipment();
        var tester = database.Resources.AddEquipment();

        var assignment = Assigned(await AssignAsync(visitId, technicianId, vehicleId, [drill, tester], ["electrical"], before: 15, after: 30));

        Assert.Equal(AssignmentStatus.Active, assignment.Status);
        Assert.Equal(15, assignment.TravelBufferBeforeMinutes);
        Assert.Equal(30, assignment.TravelBufferAfterMinutes);

        var saved = Assert.Single(await AssignmentsOfVisitAsync(visitId));
        Assert.Equal(technicianId, saved.TechnicianId);
        Assert.Equal(vehicleId, saved.VehicleId);
        Assert.Equal(new[] { drill, tester }.Order(), saved.Equipment.Select(equipment => equipment.EquipmentId).Order());

        // One reservation per resource, over the buffered window, linked to the assignment.
        var reservations = await ReservationsOfVisitAsync(visitId);
        Assert.Equal(
            new[] { (ResourceType.Technician, technicianId), (ResourceType.Vehicle, vehicleId), (ResourceType.Equipment, drill), (ResourceType.Equipment, tester) }
                .OrderBy(resource => resource.Item1).ThenBy(resource => resource.Item2),
            reservations.Select(r => (r.ResourceType, r.ResourceId)).OrderBy(resource => resource.Item1).ThenBy(resource => resource.Item2));
        Assert.All(reservations, reservation =>
        {
            Assert.Equal(_start.AddMinutes(-15), reservation.Start);
            Assert.Equal(_end.AddMinutes(30), reservation.End);
            Assert.Equal(assignment.Id, reservation.AssignmentId);
        });
        Assert.Equal(_start.AddMinutes(-15), saved.ClaimedStart);
        Assert.Equal(_end.AddMinutes(30), saved.ClaimedEnd);

        Assert.Equal([AssignmentEvents.AssignmentCreated], await EventTypesAsync(assignment.Id));
    }

    [Fact]
    public async Task Without_vehicle_or_equipment_only_the_technician_is_reserved()
    {
        var visitId = NewVisit();
        var technicianId = database.Technicians.Add();

        var assignment = Assigned(await AssignAsync(visitId, technicianId));

        var reservation = Assert.Single(await ReservationsOfVisitAsync(visitId));
        Assert.Equal((ResourceType.Technician, technicianId), (reservation.ResourceType, reservation.ResourceId));
        Assert.Null(assignment.VehicleId);
        Assert.Empty(assignment.Equipment);
        Assert.Equal(_start, reservation.Start); // zero buffer: exactly the visit
    }

    [Fact]
    public async Task A_second_active_assignment_for_the_visit_is_rejected()
    {
        var visitId = NewVisit();
        var first = Assigned(await AssignAsync(visitId, database.Technicians.Add()));

        var second = Assert.IsType<AssignOutcome.AlreadyAssigned>(await AssignAsync(visitId, database.Technicians.Add()));

        Assert.Equal(first.Id, second.ActiveAssignmentId);
        Assert.Single(await AssignmentsOfVisitAsync(visitId));
    }

    [Theory]
    [InlineData(VisitState.Completed)]
    [InlineData(VisitState.Cancelled)]
    public async Task Closed_visits_cannot_be_assigned(VisitState state)
    {
        var visitId = NewVisit(state);

        var closed = Assert.IsType<AssignOutcome.VisitClosed>(await AssignAsync(visitId, database.Technicians.Add()));

        Assert.Equal(state.ToString(), closed.VisitStatus);
        Assert.Empty(await AssignmentsOfVisitAsync(visitId));
    }

    [Fact]
    public async Task An_in_progress_visit_can_be_assigned()
    {
        Assigned(await AssignAsync(NewVisit(VisitState.InProgress), database.Technicians.Add()));
    }

    // ---------------------------------------------------------------- validation (final check)

    [Fact]
    public async Task Missing_visit_and_missing_resources_are_reported()
    {
        Assert.IsType<AssignOutcome.VisitNotFound>(await AssignAsync(Guid.NewGuid(), database.Technicians.Add()));

        var missing = Assert.IsType<AssignOutcome.ResourcesNotFound>(
            await AssignAsync(NewVisit(), Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()]));
        Assert.Equal(
            [ResourceType.Technician, ResourceType.Vehicle, ResourceType.Equipment],
            missing.Missing.Select(resource => resource.Type));
    }

    [Fact]
    public async Task Duplicate_equipment_and_invalid_buffers_are_invalid()
    {
        var drill = database.Resources.AddEquipment();

        var duplicate = Assert.IsType<AssignOutcome.Invalid>(await AssignAsync(NewVisit(), database.Technicians.Add(), equipmentIds: [drill, drill]));
        var negative = Assert.IsType<AssignOutcome.Invalid>(await AssignAsync(NewVisit(), database.Technicians.Add(), before: -1));
        var tooLong = Assert.IsType<AssignOutcome.Invalid>(await AssignAsync(NewVisit(), database.Technicians.Add(), after: 481));

        Assert.Equal(["equipmentIds"], duplicate.Errors.Keys);
        Assert.Equal(["travelBufferBeforeMinutes"], negative.Errors.Keys);
        Assert.Equal(["travelBufferAfterMinutes"], tooLong.Errors.Keys);
    }

    [Fact]
    public async Task Missing_skills_and_unavailability_reject_the_claim()
    {
        var unskilled = Assert.IsType<AssignOutcome.Rejected>(
            await AssignAsync(NewVisit(), database.Technicians.Add(skillCodes: ["HVAC"]), skills: ["Fiber"]));
        var absent = Assert.IsType<AssignOutcome.Rejected>(
            await AssignAsync(NewVisit(), database.Technicians.Add(availability: TechnicianAvailabilityState.Absence)));

        Assert.Equal(SchedulingConflictCode.MissingRequiredSkills, Assert.Single(unskilled.Reasons).Code);
        Assert.Equal(SchedulingConflictCode.TechnicianUnavailable, Assert.Single(absent.Reasons).Code);
    }

    [Fact]
    public async Task Resources_reserved_by_another_visit_reject_the_claim_and_nothing_is_written()
    {
        var technicianId = database.Technicians.Add();
        var vehicleId = database.Resources.AddVehicle();
        var drill = database.Resources.AddEquipment();
        await ReserveForOtherVisitAsync(ResourceType.Technician, technicianId, _start, _end);
        await ReserveForOtherVisitAsync(ResourceType.Vehicle, vehicleId, _start, _end);
        await ReserveForOtherVisitAsync(ResourceType.Equipment, drill, _end, _end.AddHours(1));
        var visitId = NewVisit();

        var rejected = Assert.IsType<AssignOutcome.Rejected>(await AssignAsync(visitId, technicianId, vehicleId, [drill], after: 30));

        Assert.Equal(
            [SchedulingConflictCode.TechnicianReservationConflict, SchedulingConflictCode.VehicleReservationConflict, SchedulingConflictCode.TravelBufferConflict],
            rejected.Reasons.Select(reason => reason.Code));
        Assert.Empty(await AssignmentsOfVisitAsync(visitId));
        Assert.Empty(await ReservationsOfVisitAsync(visitId));
        Assert.Equal(0, await EventsMentioningAsync(visitId));
    }

    // ---------------------------------------------------------------- concurrency

    [Fact]
    public async Task Two_concurrent_claims_of_one_technician_leave_one_winner_and_no_partial_state()
    {
        var technicianId = database.Technicians.Add();
        var firstVisit = NewVisit();
        var secondVisit = NewVisit();

        // Both requests pass the final check before either writes: only the exclusion constraint can decide.
        database.Technicians.GateProfileLookups(technicianId, callers: 2);

        var outcomes = await Task.WhenAll(AssignAsync(firstVisit, technicianId), AssignAsync(secondVisit, technicianId));

        var winner = Assert.Single(outcomes.OfType<AssignOutcome.Assigned>());
        var loser = Assert.Single(outcomes.OfType<AssignOutcome.Rejected>());
        Assert.Equal(SchedulingConflictCode.TechnicianReservationConflict, Assert.Single(loser.Reasons).Code);

        var loserVisit = winner.Assignment.VisitId == firstVisit ? secondVisit : firstVisit;
        Assert.Empty(await AssignmentsOfVisitAsync(loserVisit));
        Assert.Empty(await ReservationsOfVisitAsync(loserVisit));
        Assert.Equal(0, await EventsMentioningAsync(loserVisit));
        Assert.Single(await ReservationsOfVisitAsync(winner.Assignment.VisitId));
        Assert.Equal(1, await EventsMentioningAsync(winner.Assignment.VisitId)); // the query does find real events
    }

    [Fact]
    public async Task Two_concurrent_assignments_of_one_visit_leave_one_active()
    {
        var visitId = NewVisit();
        var firstTechnician = database.Technicians.Add();
        var secondTechnician = database.Technicians.Add();

        var outcomes = await Task.WhenAll(AssignAsync(visitId, firstTechnician), AssignAsync(visitId, secondTechnician));

        Assert.Single(outcomes.OfType<AssignOutcome.Assigned>());
        Assert.Single(outcomes.OfType<AssignOutcome.AlreadyAssigned>());
        Assert.Single(await AssignmentsOfVisitAsync(visitId));
        Assert.Single(await ReservationsOfVisitAsync(visitId));
    }

    // ---------------------------------------------------------------- reassignment

    [Fact]
    public async Task Reassignment_replaces_the_assignment_and_moves_the_reservations()
    {
        var visitId = NewVisit();
        var oldTechnician = database.Technicians.Add();
        var newTechnician = database.Technicians.Add();
        var vehicleId = database.Resources.AddVehicle();
        var old = Assigned(await AssignAsync(visitId, oldTechnician, vehicleId));

        var replacement = Assigned(await ReassignAsync(visitId, newTechnician, after: 20));

        var history = await AssignmentsOfVisitAsync(visitId);
        Assert.Equal([old.Id, replacement.Id], history.Select(assignment => assignment.Id));
        Assert.Equal(AssignmentStatus.Replaced, history[0].Status);
        Assert.Equal(replacement.Id, history[0].ReplacedByAssignmentId);
        Assert.Equal(AssignmentStatus.Active, history[1].Status);

        var reservation = Assert.Single(await ReservationsOfVisitAsync(visitId));
        Assert.Equal((ResourceType.Technician, newTechnician, replacement.Id), (reservation.ResourceType, reservation.ResourceId, reservation.AssignmentId));
        Assert.Equal(_end.AddMinutes(20), reservation.End);

        Assert.Equal([AssignmentEvents.AssignmentCreated, AssignmentEvents.AssignmentReplaced], await EventTypesAsync(old.Id));
        Assert.Equal([AssignmentEvents.AssignmentCreated], await EventTypesAsync(replacement.Id));
    }

    [Fact]
    public async Task Reassignment_may_keep_the_same_resources_for_the_same_visit()
    {
        var visitId = NewVisit();
        var technicianId = database.Technicians.Add();
        var drill = database.Resources.AddEquipment();
        Assigned(await AssignAsync(visitId, technicianId, equipmentIds: [drill]));

        // Same technician and asset, wider buffer: overlaps only this visit's own (current) reservations.
        var replacement = Assigned(await ReassignAsync(visitId, technicianId, equipmentIds: [drill], before: 30, after: 30));

        var reservations = await ReservationsOfVisitAsync(visitId);
        Assert.Equal(2, reservations.Count);
        Assert.All(reservations, reservation => Assert.Equal(replacement.Id, reservation.AssignmentId));
    }

    [Fact]
    public async Task A_failed_reassignment_leaves_the_old_assignment_and_reservations_untouched()
    {
        var visitId = NewVisit();
        var technicianId = database.Technicians.Add();
        var busyTechnician = database.Technicians.Add();
        var old = Assigned(await AssignAsync(visitId, technicianId));
        await ReserveForOtherVisitAsync(ResourceType.Technician, busyTechnician, _start, _end);

        var rejected = Assert.IsType<AssignOutcome.Rejected>(await ReassignAsync(visitId, busyTechnician));

        Assert.Equal(SchedulingConflictCode.TechnicianReservationConflict, Assert.Single(rejected.Reasons).Code);
        var active = Assert.Single(await AssignmentsOfVisitAsync(visitId));
        Assert.Equal((old.Id, AssignmentStatus.Active), (active.Id, active.Status));
        var reservation = Assert.Single(await ReservationsOfVisitAsync(visitId));
        Assert.Equal((technicianId, old.Id), (reservation.ResourceId, reservation.AssignmentId));
        Assert.Equal([AssignmentEvents.AssignmentCreated], await EventTypesAsync(old.Id));
    }

    [Fact]
    public async Task A_reassignment_that_loses_a_race_rolls_back_completely()
    {
        var visitId = NewVisit();
        var otherVisit = NewVisit();
        var technicianId = database.Technicians.Add();
        var contested = database.Technicians.Add();
        var old = Assigned(await AssignAsync(visitId, technicianId));

        // The reassignment and another visit's assignment both pass the check, then race for the same technician.
        database.Technicians.GateProfileLookups(contested, callers: 2);
        var outcomes = await Task.WhenAll(ReassignAsync(visitId, contested), AssignAsync(otherVisit, contested));

        Assert.Single(outcomes.OfType<AssignOutcome.Assigned>());
        Assert.Single(outcomes.OfType<AssignOutcome.Rejected>());

        if (outcomes[0] is AssignOutcome.Rejected)
        {
            // The reassignment lost: the visit keeps its old assignment and reservation, nothing half-done.
            var active = Assert.Single(await AssignmentsOfVisitAsync(visitId));
            Assert.Equal((old.Id, AssignmentStatus.Active), (active.Id, active.Status));
            Assert.Equal(technicianId, Assert.Single(await ReservationsOfVisitAsync(visitId)).ResourceId);
        }
        else
        {
            Assert.Empty(await AssignmentsOfVisitAsync(otherVisit));
            Assert.Empty(await ReservationsOfVisitAsync(otherVisit));
        }
    }

    [Fact]
    public async Task Reassigning_a_visit_without_an_active_assignment_is_reported()
    {
        Assert.IsType<AssignOutcome.NoActiveAssignment>(await ReassignAsync(NewVisit(), database.Technicians.Add()));
    }

    // ---------------------------------------------------------------- cancellation

    [Fact]
    public async Task Cancel_keeps_the_assignment_as_history_and_releases_its_reservations()
    {
        var visitId = NewVisit();
        var technicianId = database.Technicians.Add();
        var assignment = Assigned(await AssignAsync(visitId, technicianId, equipmentIds: [database.Resources.AddEquipment()]));

        var cancelled = Assert.IsType<CancelAssignmentOutcome.Cancelled>(await CancelAsync(visitId));

        Assert.Equal(AssignmentStatus.Cancelled, cancelled.Assignment.Status);
        var saved = Assert.Single(await AssignmentsOfVisitAsync(visitId));
        Assert.Equal(AssignmentStatus.Cancelled, saved.Status);
        Assert.Empty(await ReservationsOfVisitAsync(visitId));
        Assert.Equal([AssignmentEvents.AssignmentCreated, AssignmentEvents.AssignmentCancelled], await EventTypesAsync(assignment.Id));

        // The technician is free again for another visit, and this visit can be assigned anew (to someone else).
        Assigned(await AssignAsync(NewVisit(), technicianId));
        Assigned(await AssignAsync(visitId, database.Technicians.Add()));
    }

    [Fact]
    public async Task A_second_cancel_changes_nothing()
    {
        var visitId = NewVisit();
        var assignment = Assigned(await AssignAsync(visitId, database.Technicians.Add()));
        Assert.IsType<CancelAssignmentOutcome.Cancelled>(await CancelAsync(visitId));

        Assert.IsType<CancelAssignmentOutcome.NothingToCancel>(await CancelAsync(visitId));
        Assert.Equal([AssignmentEvents.AssignmentCreated, AssignmentEvents.AssignmentCancelled], await EventTypesAsync(assignment.Id));
        Assert.IsType<CancelAssignmentOutcome.VisitNotFound>(await CancelAsync(Guid.NewGuid()));
    }

    // ---------------------------------------------------------------- queries

    [Fact]
    public async Task Active_and_history_queries_reflect_the_lifecycle()
    {
        var visitId = NewVisit();
        var first = Assigned(await AssignAsync(visitId, database.Technicians.Add()));
        var second = Assigned(await ReassignAsync(visitId, database.Technicians.Add()));

        await using var scope = database.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AssignmentService>();
        var (found, active) = await service.GetActiveForVisitAsync(visitId, Cancellation);
        var history = await service.GetHistoryForVisitAsync(visitId, Cancellation);

        Assert.True(found);
        Assert.Equal(second.Id, active!.Id);
        Assert.Equal([first.Id, second.Id], history!.Select(assignment => assignment.Id));
        Assert.Null(await service.GetHistoryForVisitAsync(Guid.NewGuid(), Cancellation));
        Assert.False((await service.GetActiveForVisitAsync(Guid.NewGuid(), Cancellation)).VisitFound);
    }
}
