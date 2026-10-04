using CrewCall.Scheduling.Reservations;

namespace CrewCall.Scheduling.Checks;

/// <summary>Could this technician, vehicle and equipment take a visit in [Start, End)? Read-only: nothing is reserved.</summary>
/// <param name="VisitId">The visit being (re)planned: its own reservations are ignored, so it never conflicts with itself.</param>
/// <param name="TravelBufferBeforeMinutes">Time kept free before the visit, 0–480. Defaults to 0.</param>
/// <param name="TravelBufferAfterMinutes">Time kept free after the visit, 0–480. Defaults to 0.</param>
public sealed record SchedulingCheck(
    Guid? VisitId,
    Guid? TechnicianId,
    Guid? VehicleId,
    IReadOnlyCollection<Guid>? EquipmentIds,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    IReadOnlyCollection<string>? RequiredSkillCodes,
    int? TravelBufferBeforeMinutes,
    int? TravelBufferAfterMinutes);

public enum SchedulingConflictCode
{
    /// <summary>F# CandidateInactive.</summary>
    TechnicianInactive,

    /// <summary>F# OutsideAvailability: Workforce reports the technician unavailable (details: the reasons).</summary>
    TechnicianUnavailable,

    /// <summary>F# MissingRequiredSkills (details: the missing codes).</summary>
    MissingRequiredSkills,

    /// <summary>The technician is reserved during the visit.</summary>
    TechnicianReservationConflict,

    /// <summary>The vehicle is reserved during the visit.</summary>
    VehicleReservationConflict,

    /// <summary>An equipment asset is reserved during the visit (one reason per asset and reservation).</summary>
    EquipmentReservationConflict,

    /// <summary>A resource is reserved during the travel buffer before or after the visit, but not during the visit.</summary>
    TravelBufferConflict
}

/// <summary>One reason the visit cannot be planned as requested.</summary>
/// <param name="ResourceType">The resource concerned, for reservation conflicts and technician reasons.</param>
/// <param name="ReservationId">The clashing reservation, for reservation and travel-buffer conflicts.</param>
/// <param name="Details">Missing skill codes, or Workforce's unavailability reasons and detail.</param>
public sealed record SchedulingConflict(
    SchedulingConflictCode Code,
    string Message,
    ResourceType? ResourceType,
    Guid? RelatedResourceId,
    Guid? ReservationId,
    DateTimeOffset? ReservedStart,
    DateTimeOffset? ReservedEnd,
    IReadOnlyList<string> Details);

/// <param name="EffectiveStart">Start minus the travel buffer before: where resource conflicts are checked from.</param>
/// <param name="EffectiveEnd">End plus the travel buffer after: where resource conflicts are checked to.</param>
public sealed record SchedulingCheckResult(
    Guid TechnicianId,
    Guid? VehicleId,
    IReadOnlyList<Guid> EquipmentIds,
    Guid? VisitId,
    DateTimeOffset Start,
    DateTimeOffset End,
    DateTimeOffset EffectiveStart,
    DateTimeOffset EffectiveEnd,
    IReadOnlyList<SchedulingConflict> Reasons)
{
    /// <summary>True only when there is no reason at all.</summary>
    public bool IsFeasible => Reasons.Count == 0;
}

public abstract record SchedulingCheckOutcome
{
    private SchedulingCheckOutcome()
    {
    }

    public sealed record Checked(SchedulingCheckResult Result) : SchedulingCheckOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : SchedulingCheckOutcome;

    /// <summary>Every requested resource that does not exist (technician, vehicle, equipment).</summary>
    public sealed record ResourcesNotFound(IReadOnlyList<(ResourceType Type, Guid ResourceId)> Missing) : SchedulingCheckOutcome;
}
