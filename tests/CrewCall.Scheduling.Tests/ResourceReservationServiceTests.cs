using CrewCall.Persistence;
using CrewCall.Scheduling.Reservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Scheduling.Tests;

public sealed class ResourceReservationServiceTests(SchedulingDatabase database)
{
    private static readonly DateTimeOffset _day = new(2026, 7, 6, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private async Task<CreateReservationOutcome> ReserveAsync(
        string type, Guid resourceId, int startHour, int endHour, Guid? visitId = null)
    {
        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ResourceReservationService>().CreateAsync(
            new CreateReservation(type, resourceId, visitId, _day.AddHours(startHour), _day.AddHours(endHour)), Cancellation);
    }

    [Fact]
    public async Task Technician_vehicle_and_equipment_reservations_can_be_created()
    {
        var technicianId = database.Technicians.Add();
        var vehicleId = database.Resources.AddVehicle();
        var equipmentId = database.Resources.AddEquipment();

        var technician = Assert.IsType<CreateReservationOutcome.Created>(await ReserveAsync("Technician", technicianId, 8, 10));
        var vehicle = Assert.IsType<CreateReservationOutcome.Created>(await ReserveAsync("vehicle", vehicleId, 8, 10));
        var equipment = Assert.IsType<CreateReservationOutcome.Created>(await ReserveAsync("EQUIPMENT", equipmentId, 8, 10));

        Assert.Equal(ResourceType.Technician, technician.Reservation.ResourceType);
        Assert.Equal(ResourceType.Vehicle, vehicle.Reservation.ResourceType);
        Assert.Equal(ResourceType.Equipment, equipment.Reservation.ResourceType);

        await using var scope = database.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<ISchedulingDbContext>().ResourceReservations
            .AsNoTracking()
            .SingleAsync(reservation => reservation.Id == equipment.Reservation.Id, Cancellation);
        Assert.Equal(equipmentId, saved.ResourceId);
        Assert.Equal(_day.AddHours(8), saved.Start);
        Assert.Equal(TimeSpan.Zero, saved.Start.Offset);
    }

    [Theory]
    [InlineData(8, 12, 11, 14)] // overlaps the end
    [InlineData(8, 12, 6, 9)]   // overlaps the start
    [InlineData(8, 12, 9, 10)]  // inside
    [InlineData(8, 12, 7, 13)]  // contains
    public async Task An_overlapping_reservation_of_the_same_resource_is_rejected(int existingStart, int existingEnd, int start, int end)
    {
        var vehicleId = database.Resources.AddVehicle();
        var existing = Assert.IsType<CreateReservationOutcome.Created>(await ReserveAsync("Vehicle", vehicleId, existingStart, existingEnd));

        var overlap = Assert.IsType<CreateReservationOutcome.Overlaps>(await ReserveAsync("Vehicle", vehicleId, start, end));

        Assert.Equal(existing.Reservation.Id, overlap.ExistingReservationId);
    }

    [Fact]
    public async Task Touching_reservations_of_the_same_resource_are_allowed()
    {
        var technicianId = database.Technicians.Add();
        Assert.IsType<CreateReservationOutcome.Created>(await ReserveAsync("Technician", technicianId, 8, 12));

        Assert.IsType<CreateReservationOutcome.Created>(await ReserveAsync("Technician", technicianId, 12, 14));
        Assert.IsType<CreateReservationOutcome.Created>(await ReserveAsync("Technician", technicianId, 6, 8));
    }

    [Fact]
    public async Task Different_resources_may_overlap_including_the_same_type_with_another_id()
    {
        var technicianId = database.Technicians.Add();
        var vehicleId = database.Resources.AddVehicle();
        var firstDrill = database.Resources.AddEquipment();
        var secondDrill = database.Resources.AddEquipment();

        Assert.IsType<CreateReservationOutcome.Created>(await ReserveAsync("Technician", technicianId, 8, 12));
        Assert.IsType<CreateReservationOutcome.Created>(await ReserveAsync("Vehicle", vehicleId, 8, 12));
        Assert.IsType<CreateReservationOutcome.Created>(await ReserveAsync("Equipment", firstDrill, 8, 12));
        Assert.IsType<CreateReservationOutcome.Created>(await ReserveAsync("Equipment", secondDrill, 8, 12));
    }

    [Fact]
    public async Task The_database_itself_rejects_an_overlap_and_accepts_touching_and_other_types()
    {
        // Written directly, past the service's own check: the exclusion constraint is the final guard.
        var resourceId = Guid.NewGuid();
        await SaveDirectlyAsync(new ResourceReservation(Guid.NewGuid(), ResourceType.Equipment, resourceId, null, _day.AddHours(8), _day.AddHours(12)));

        await SaveDirectlyAsync(new ResourceReservation(Guid.NewGuid(), ResourceType.Equipment, resourceId, null, _day.AddHours(12), _day.AddHours(13)));
        await SaveDirectlyAsync(new ResourceReservation(Guid.NewGuid(), ResourceType.Vehicle, resourceId, null, _day.AddHours(8), _day.AddHours(12)));
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            SaveDirectlyAsync(new ResourceReservation(Guid.NewGuid(), ResourceType.Equipment, resourceId, null, _day.AddHours(11), _day.AddHours(14))));
    }

    [Fact]
    public async Task A_reservation_of_a_missing_resource_is_rejected()
    {
        var outcome = await ReserveAsync("Vehicle", Guid.NewGuid(), 8, 10);

        var notFound = Assert.IsType<CreateReservationOutcome.ResourceNotFound>(outcome);
        Assert.Equal(ResourceType.Vehicle, notFound.ResourceType);
    }

    [Theory]
    [InlineData("Room", 8, 10, "resourceType")]
    [InlineData("1", 8, 10, "resourceType")]
    [InlineData("Vehicle", 10, 8, "end")]
    [InlineData("Vehicle", 10, 10, "end")]
    public async Task Invalid_reservations_are_rejected(string type, int startHour, int endHour, string field)
    {
        var invalid = Assert.IsType<CreateReservationOutcome.Invalid>(
            await ReserveAsync(type, database.Resources.AddVehicle(), startHour, endHour));

        Assert.Equal([field], invalid.Errors.Keys);
    }

    [Fact]
    public async Task Remove_deletes_the_reservation_and_is_idempotent()
    {
        var vehicleId = database.Resources.AddVehicle();
        var created = Assert.IsType<CreateReservationOutcome.Created>(await ReserveAsync("Vehicle", vehicleId, 8, 10));

        await using (var scope = database.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ResourceReservationService>();
            await service.RemoveAsync(created.Reservation.Id, Cancellation);
            await service.RemoveAsync(created.Reservation.Id, Cancellation);
        }

        Assert.IsType<CreateReservationOutcome.Created>(await ReserveAsync("Vehicle", vehicleId, 8, 10));
    }

    [Fact]
    public async Task Conflicts_exclude_the_reservations_of_the_visit_being_checked()
    {
        var technicianId = database.Technicians.Add();
        var visitId = Guid.NewGuid();
        Assert.IsType<CreateReservationOutcome.Created>(await ReserveAsync("Technician", technicianId, 8, 10, visitId));
        var visit = CoreMapping.ToRange(_day.AddHours(9), _day.AddHours(11));

        await using var scope = database.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ResourceReservationService>();
        var withSelf = await service.GetConflictsAsync([(ResourceType.Technician, technicianId)], visit, visit, null, Cancellation);
        var withoutSelf = await service.GetConflictsAsync([(ResourceType.Technician, technicianId)], visit, visit, visitId, Cancellation);

        Assert.Single(withSelf);
        Assert.Empty(withoutSelf);
    }

    private async Task SaveDirectlyAsync(ResourceReservation reservation)
    {
        await using var scope = database.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrewCallDbContext>();
        db.ResourceReservations.Add(reservation);
        await db.SaveChangesAsync(Cancellation);
    }
}
