using System.Collections.Concurrent;
using CrewCall.Scheduling.Ports;

namespace CrewCall.Scheduling.Tests;

/// <summary>Stands in for Workforce: each test registers its own technicians (unique ids), so tests may run in parallel.</summary>
public sealed class FakeTechnicianSource : ITechnicianSchedulingSource
{
    private readonly ConcurrentDictionary<Guid, TechnicianSchedulingProfile> _profiles = new();
    private readonly ConcurrentDictionary<Guid, Gate> _gates = new();

    /// <summary>
    /// Holds the first <paramref name="callers"/> profile lookups for the technician until all of them have arrived, so
    /// concurrent requests pass the check together and race to claim (the database must then decide).
    /// </summary>
    public void GateProfileLookups(Guid technicianId, int callers) => _gates[technicianId] = new Gate(callers);

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

    public async Task<TechnicianSchedulingProfile?> GetProfileAsync(
        Guid technicianId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        if (_gates.TryGetValue(technicianId, out var gate))
        {
            await gate.ArriveAsync(cancellationToken);
        }

        return _profiles.GetValueOrDefault(technicianId);
    }

    private sealed class Gate(int callers)
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = callers;

        public Task ArriveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Decrement(ref _remaining) <= 0)
            {
                _open.TrySetResult();
            }

            return _open.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
    }

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

/// <summary>Stands in for WorkOrders' visits.</summary>
public sealed class FakeVisitSource : IVisitSchedulingSource
{
    private readonly ConcurrentDictionary<Guid, VisitSchedulingInfo> _visits = new();

    public Guid Add(DateTimeOffset start, DateTimeOffset end, VisitState state = VisitState.Planned)
    {
        var id = Guid.NewGuid();
        _visits[id] = new VisitSchedulingInfo(id, start, end, state);
        return id;
    }

    public Task<VisitSchedulingInfo?> GetVisitAsync(Guid visitId, CancellationToken cancellationToken) =>
        Task.FromResult(_visits.GetValueOrDefault(visitId));
}
