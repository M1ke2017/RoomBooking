namespace CrewCall.Scheduling.Assignments;

public enum AssignmentStatus
{
    Active,
    Replaced,
    Cancelled
}

/// <summary>
/// Who and what is assigned to a visit: a technician, an optional vehicle and any number of equipment assets, with the
/// travel buffers the resources were claimed with. Creating an assignment claims its resources (resource reservations)
/// in the same transaction. A visit has at most one Active assignment; earlier ones stay as history (Replaced or
/// Cancelled) and are never deleted.
/// </summary>
/// <remarks>
/// VisitId, TechnicianId, VehicleId and the equipment ids refer to other modules' data and have no foreign keys
/// (ADR-0001, ADR-0008). The visit itself stores no resource fields.
/// </remarks>
public sealed class Assignment
{
    private readonly List<AssignmentEquipment> _equipment = [];

    internal Assignment(
        Guid id,
        Guid visitId,
        Guid technicianId,
        Guid? vehicleId,
        IEnumerable<Guid> equipmentIds,
        int travelBufferBeforeMinutes,
        int travelBufferAfterMinutes,
        DateTimeOffset claimedStart,
        DateTimeOffset claimedEnd,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        VisitId = visitId;
        TechnicianId = technicianId;
        VehicleId = vehicleId;
        _equipment.AddRange(equipmentIds.Select(equipmentId => new AssignmentEquipment(id, equipmentId)));
        TravelBufferBeforeMinutes = travelBufferBeforeMinutes;
        TravelBufferAfterMinutes = travelBufferAfterMinutes;
        ClaimedStart = claimedStart;
        ClaimedEnd = claimedEnd;
        Status = AssignmentStatus.Active;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    // For EF Core materialization.
    private Assignment()
    {
    }

    public Guid Id { get; private set; }

    public Guid VisitId { get; private set; }

    public Guid TechnicianId { get; private set; }

    public Guid? VehicleId { get; private set; }

    public IReadOnlyList<AssignmentEquipment> Equipment => _equipment;

    public int TravelBufferBeforeMinutes { get; private set; }

    public int TravelBufferAfterMinutes { get; private set; }

    /// <summary>The window the resources were reserved for: visit start minus the before buffer (UTC).</summary>
    public DateTimeOffset ClaimedStart { get; private set; }

    /// <summary>Visit end plus the after buffer (UTC).</summary>
    public DateTimeOffset ClaimedEnd { get; private set; }

    public AssignmentStatus Status { get; private set; }

    /// <summary>The assignment that replaced this one, when Status is Replaced.</summary>
    public Guid? ReplacedByAssignmentId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    internal void ReplaceWith(Guid newAssignmentId, DateTimeOffset now)
    {
        Status = AssignmentStatus.Replaced;
        ReplacedByAssignmentId = newAssignmentId;
        UpdatedAtUtc = now;
    }

    /// <summary>
    /// The visit moved (Sprint 15): the same resources, claimed over the new window. The assignment keeps its identity;
    /// its reservations move with it (<see cref="Reservations.ResourceReservation.MoveTo"/>).
    /// </summary>
    internal void MoveClaim(DateTimeOffset claimedStart, DateTimeOffset claimedEnd, DateTimeOffset now)
    {
        ClaimedStart = claimedStart;
        ClaimedEnd = claimedEnd;
        UpdatedAtUtc = now;
    }

    internal void Cancel(DateTimeOffset now)
    {
        Status = AssignmentStatus.Cancelled;
        UpdatedAtUtc = now;
    }
}

/// <summary>One equipment asset of an assignment. Each asset is a separate resource, never a quantity.</summary>
public sealed class AssignmentEquipment
{
    internal AssignmentEquipment(Guid assignmentId, Guid equipmentId)
    {
        AssignmentId = assignmentId;
        EquipmentId = equipmentId;
    }

    public Guid AssignmentId { get; private set; }

    public Guid EquipmentId { get; private set; }
}
