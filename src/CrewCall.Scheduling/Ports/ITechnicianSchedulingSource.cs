namespace CrewCall.Scheduling.Ports;

/// <summary>
/// What Scheduling needs to know about a technician, provided by the Workforce module through the composition root.
/// Scheduling never reads Workforce tables and never re-implements working hours, absences, holidays or time zones:
/// it consumes Workforce's own availability decision.
/// </summary>
public interface ITechnicianSchedulingSource
{
    /// <summary>The technician's profile for [start, end), or null when the technician does not exist.</summary>
    /// <remarks>The interval is at most <see cref="Checks.SchedulingCheckService.MaxVisitLength"/> long.</remarks>
    Task<TechnicianSchedulingProfile?> GetProfileAsync(
        Guid technicianId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(Guid technicianId, CancellationToken cancellationToken);

    /// <summary>
    /// With <paramref name="technicianIds"/>: those technicians (any status) that exist. Without: active technicians,
    /// ordered by id. At most <paramref name="limit"/> either way.
    /// </summary>
    Task<IReadOnlyList<TechnicianSummary>> ListTechniciansAsync(
        IReadOnlyCollection<Guid>? technicianIds, int limit, CancellationToken cancellationToken);

    Task<bool> TeamExistsAsync(Guid teamId, CancellationToken cancellationToken);
}

/// <param name="TeamId">The technician's current team, if any.</param>
public sealed record TechnicianSummary(Guid TechnicianId, string DisplayName, bool IsActive, Guid? TeamId);

/// <summary>Workforce's answer for one technician and one interval.</summary>
/// <param name="SkillCodes">Codes of the technician's active skills (skills without a code cannot be required).</param>
/// <param name="Availability">Workforce's availability decision for the whole interval.</param>
/// <param name="AvailabilityDetail">Workforce's explanation, e.g. the holiday or absence type; null when available.</param>
public sealed record TechnicianSchedulingProfile(
    Guid TechnicianId,
    bool IsActive,
    IReadOnlyCollection<string> SkillCodes,
    TechnicianAvailabilityState Availability,
    string? AvailabilityDetail);

/// <summary>Mirrors Workforce's availability reasons without depending on the Workforce module.</summary>
public enum TechnicianAvailabilityState
{
    Available,
    Inactive,
    OutsideWorkingHours,
    Absence,
    Holiday
}
