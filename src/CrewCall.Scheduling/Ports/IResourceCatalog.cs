namespace CrewCall.Scheduling.Ports;

/// <summary>Which vehicles and equipment assets exist, provided by the Resources module through the composition root.</summary>
public interface IResourceCatalog
{
    Task<bool> VehicleExistsAsync(Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>The ids among <paramref name="equipmentIds"/> that do not exist.</summary>
    Task<IReadOnlyCollection<Guid>> FindMissingEquipmentAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken);
}
