using Microsoft.EntityFrameworkCore;

namespace CrewCall.Resources.Vehicles;

public sealed class VehicleService(IResourcesDbContext db)
{
    public async Task<CreateVehicleOutcome> CreateAsync(CreateVehicle command, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var registrationNumber = errors
            .Required("registrationNumber", command.RegistrationNumber, Vehicle.RegistrationNumberMaxLength)
            ?.ToUpperInvariant();
        var displayName = errors.Required("displayName", command.DisplayName, Vehicle.DisplayNameMaxLength);
        var vehicleType = errors.Optional("vehicleType", command.VehicleType, Vehicle.VehicleTypeMaxLength);

        if (errors.Any)
        {
            return new CreateVehicleOutcome.Invalid(errors.ToDictionary());
        }

        if (await RegistrationExistsAsync(registrationNumber!, cancellationToken))
        {
            return new CreateVehicleOutcome.RegistrationNumberAlreadyExists(registrationNumber!);
        }

        var vehicle = new Vehicle(Guid.CreateVersion7(), registrationNumber!, displayName!, vehicleType, command.IsActive ?? true);
        db.Vehicles.Add(vehicle);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent request may have registered the same number; the unique index rejected this one.
            if (await RegistrationExistsAsync(registrationNumber!, cancellationToken))
            {
                return new CreateVehicleOutcome.RegistrationNumberAlreadyExists(registrationNumber!);
            }

            throw;
        }

        return new CreateVehicleOutcome.Created(vehicle);
    }

    public async Task<IReadOnlyList<Vehicle>> ListAsync(CancellationToken cancellationToken) =>
        await db.Vehicles
            .AsNoTracking()
            .OrderBy(vehicle => vehicle.RegistrationNumber)
            .ToListAsync(cancellationToken);

    private Task<bool> RegistrationExistsAsync(string registrationNumber, CancellationToken cancellationToken) =>
        db.Vehicles.AnyAsync(vehicle => vehicle.RegistrationNumber == registrationNumber, cancellationToken);
}
