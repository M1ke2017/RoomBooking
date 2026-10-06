using System.Collections.Concurrent;
using CrewCall.Scheduling.Ports;

namespace CrewCall.Scheduling.Tests;

/// <summary>Stands in for Workforce: each test registers its own technicians (unique ids), so tests may run in parallel.</summary>
public sealed class FakeTechnicianSource : ITechnicianSchedulingSource
{
    private readonly ConcurrentDictionary<Guid, TechnicianSchedulingProfile> _profiles = new();
    private readonly ConcurrentDictionary<Guid, TechnicianSummary> _summaries = new();
    private readonly ConcurrentDictionary<Guid, byte> _teams = new();
    private readonly ConcurrentDictionary<Guid, Gate> _gates = new();

    /// <summary>Registers a named technician, optionally in a team; otherwise like <see cref="Add"/>.</summary>
    public Guid AddTechnician(
        string name,
        Guid? teamId = null,
        bool isActive = true,
        TechnicianAvailabilityState availability = TechnicianAvailabilityState.Available,
        params string[] skillCodes)
    {
        var id = Add(isActive, availability, null, skillCodes);
        _summaries[id] = new TechnicianSummary(id, name, isActive, teamId);
        return id;
    }

    public Guid AddTeam()
    {
        var id = Guid.NewGuid();
        _teams[id] = 0;
        return id;
    }

    public Task<IReadOnlyList<TechnicianSummary>> ListTechniciansAsync(
        IReadOnlyCollection<Guid>? technicianIds, int limit, CancellationToken cancellationToken)
    {
        IEnumerable<TechnicianSummary> all = _profiles.Keys.Select(Summary);
        var selected = technicianIds is null
            ? all.Where(summary => summary.IsActive)
            : all.Where(summary => technicianIds.Contains(summary.TechnicianId));
        return Task.FromResult<IReadOnlyList<TechnicianSummary>>(selected.OrderBy(summary => summary.TechnicianId).Take(limit).ToList());
    }

    public Task<bool> TeamExistsAsync(Guid teamId, CancellationToken cancellationToken) => Task.FromResult(_teams.ContainsKey(teamId));

    private TechnicianSummary Summary(Guid id) =>
        _summaries.TryGetValue(id, out var summary) ? summary : new TechnicianSummary(id, "Technician", _profiles[id].IsActive, null);

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
