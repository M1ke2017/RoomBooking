using CrewCall.Scheduling.Checks;
using CrewCall.Scheduling.Reservations;

namespace CrewCall.Scheduling.Assignments;

/// <summary>Assigns resources to a visit (POST /api/visits/{visitId}/assignment).</summary>
/// <param name="RequiredSkillCodes">Skills the technician must have for this visit. Checked, not stored.</param>
public sealed record CreateAssignment(
    Guid VisitId,
    Guid? TechnicianId,
    Guid? VehicleId,
    IReadOnlyCollection<Guid>? EquipmentIds,
    IReadOnlyCollection<string>? RequiredSkillCodes,
    int? TravelBufferBeforeMinutes,
    int? TravelBufferAfterMinutes);

/// <summary>Replaces the visit's active assignment with new resources. Same fields as <see cref="CreateAssignment"/>.</summary>
public sealed record ReassignVisit(
    Guid VisitId,
    Guid? TechnicianId,
    Guid? VehicleId,
    IReadOnlyCollection<Guid>? EquipmentIds,
    IReadOnlyCollection<string>? RequiredSkillCodes,
    int? TravelBufferBeforeMinutes,
    int? TravelBufferAfterMinutes);

/// <summary>The resources to claim for a visit, as every claim path (create, reassign, incident dispatch) receives them.</summary>
internal sealed record ClaimRequest(
    Guid? TechnicianId,
    Guid? VehicleId,
    IReadOnlyCollection<Guid>? EquipmentIds,
    IReadOnlyCollection<string>? RequiredSkillCodes,
    int? TravelBufferBeforeMinutes,
    int? TravelBufferAfterMinutes);

/// <summary>The outcome of creating or replacing an assignment.</summary>
public abstract record AssignOutcome
{
    private AssignOutcome()
    {
    }

    public sealed record Assigned(Assignment Assignment) : AssignOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : AssignOutcome;

    public sealed record VisitNotFound(Guid VisitId) : AssignOutcome;

    public sealed record ResourcesNotFound(IReadOnlyList<(ResourceType Type, Guid ResourceId)> Missing) : AssignOutcome;

    /// <summary>The visit is Completed or Cancelled.</summary>
    public sealed record VisitClosed(Guid VisitId, string VisitStatus) : AssignOutcome;

    /// <summary>Create only: the visit already has an active assignment (use reassign).</summary>
    public sealed record AlreadyAssigned(Guid VisitId, Guid ActiveAssignmentId) : AssignOutcome;

    /// <summary>Reassign only: the visit has no active assignment to replace.</summary>
    public sealed record NoActiveAssignment(Guid VisitId) : AssignOutcome;

    /// <summary>The final check failed: every reason, as the scheduling check reports them.</summary>
    public sealed record Rejected(IReadOnlyList<SchedulingConflict> Reasons) : AssignOutcome;

    /// <summary>The active assignment changed concurrently (replaced or cancelled by another request).</summary>
    public sealed record ConcurrentChange(Guid VisitId) : AssignOutcome;
}

public abstract record CancelAssignmentOutcome
{
    private CancelAssignmentOutcome()
    {
    }

    public sealed record Cancelled(Assignment Assignment) : CancelAssignmentOutcome;

    /// <summary>No active assignment: nothing to cancel, nothing changed (idempotent).</summary>
    public sealed record NothingToCancel(Guid VisitId) : CancelAssignmentOutcome;

    public sealed record VisitNotFound(Guid VisitId) : CancelAssignmentOutcome;

    public sealed record ConcurrentChange(Guid VisitId) : CancelAssignmentOutcome;
}
