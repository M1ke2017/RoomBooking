using CrewCall.Resources;
using CrewCall.Scheduling.Ports;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.Api.SchedulingAdapters;

/// <summary>Provides Scheduling with the existence of Resources-owned vehicles and equipment (see ADR-0001).</summary>
internal sealed class ResourcesCatalog(IResourcesDbContext resources) : IResourceCatalog
{
    public Task<bool> VehicleExistsAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        resources.Vehicles.AnyAsync(vehicle => vehicle.Id == vehicleId, cancellationToken);

    public async Task<IReadOnlyCollection<Guid>> FindMissingEquipmentAsync(
        IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken)
    {
        var ids = equipmentIds.ToList();
        var existing = await resources.Equipment
            .Where(item => ids.Contains(item.Id))
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);

        return ids.Except(existing).ToList();
    }
}
