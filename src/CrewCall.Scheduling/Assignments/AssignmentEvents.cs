namespace CrewCall.Scheduling.Assignments;

/// <summary>
/// Operational events recorded by the Scheduling module, appended in the same transaction as the assignment change
/// they describe (ADR-0006).
/// </summary>
public static class AssignmentEvents
{
    public const string AssignmentAggregate = "assignment";

    public const string AssignmentCreated = nameof(AssignmentCreated);
    public const string AssignmentReplaced = nameof(AssignmentReplaced);
    public const string AssignmentCancelled = nameof(AssignmentCancelled);
}

public sealed record AssignmentCreatedPayload(
    Guid AssignmentId,
    Guid VisitId,
    Guid TechnicianId,
    Guid? VehicleId,
    IReadOnlyList<Guid> EquipmentIds,
    int TravelBufferBeforeMinutes,
    int TravelBufferAfterMinutes,
    DateTimeOffset ClaimedStart,
    DateTimeOffset ClaimedEnd);

public sealed record AssignmentReplacedPayload(Guid OldAssignmentId, Guid NewAssignmentId, Guid VisitId);

public sealed record AssignmentCancelledPayload(Guid AssignmentId, Guid VisitId);
