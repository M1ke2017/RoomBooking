using System.Collections.Concurrent;
using CrewCall.Scheduling.Ports;

namespace CrewCall.Scheduling.Tests;

/// <summary>Stands in for Workforce: each test registers its own technicians (unique ids), so tests may run in parallel.</summary>
public sealed class FakeTechnicianSource : ITechnicianSchedulingSource
{
    private readonly ConcurrentDictionary<Guid, TechnicianSchedulingProfile> _profiles = new();

    /// <summary>Registers a technician whose Workforce answer is the same for every interval.</summary>
    public Guid Add(
        bool isActive = true,
        TechnicianAvailabilityState availability = TechnicianAvailabilityState.Available,
        string? detail = null,
        params string[] skillCodes)
    {
        var id = Guid.NewGuid();
        _profiles[id] = new TechnicianSchedulingProfile(id, isActive, skillCodes, availability, detail);
        return id;
    }

    public Task<TechnicianSchedulingProfile?> GetProfileAsync(
        Guid technicianId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken) =>
        Task.FromResult(_profiles.GetValueOrDefault(technicianId));

    public Task<bool> ExistsAsync(Guid technicianId, CancellationToken cancellationToken) =>
        Task.FromResult(_profiles.ContainsKey(technicianId));
}

/// <summary>Stands in for Resources.</summary>
public sealed class FakeResourceCatalog : IResourceCatalog
{
    private readonly ConcurrentDictionary<Guid, byte> _vehicles = new();
    private readonly ConcurrentDictionary<Guid, byte> _equipment = new();

    public Guid AddVehicle()
    {
        var id = Guid.NewGuid();
        _vehicles[id] = 0;
        return id;
    }

    public Guid AddEquipment()
    {
        var id = Guid.NewGuid();
        _equipment[id] = 0;
        return id;
    }

    public Task<bool> VehicleExistsAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        Task.FromResult(_vehicles.ContainsKey(vehicleId));

    public Task<IReadOnlyCollection<Guid>> FindMissingEquipmentAsync(
        IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<Guid>>(equipmentIds.Where(id => !_equipment.ContainsKey(id)).ToList());
}
