using CrewCall.Scheduling.Core;
using CrewCall.Scheduling.Ports;
using CrewCall.Scheduling.Reservations;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace CrewCall.Scheduling;

/// <summary>
/// Maps module data to the pure F# Scheduling.Core inputs. The only place C# builds F# types, so the rest of the module
/// works with plain C# records.
/// </summary>
public static class CoreMapping
{
    /// <summary>A range already validated as non-empty (start before end).</summary>
    public static TimeRange ToRange(DateTimeOffset start, DateTimeOffset end)
    {
        var result = TimeRangeModule.create(start, end);
        return result.IsOk
            ? result.ResultValue
            : throw new ArgumentException($"Expected start {start:O} before end {end:O}.", nameof(end));
    }

    /// <summary>
    /// The technician as a scheduling candidate for <paramref name="visit"/>. Workforce's availability decision covers the
    /// whole visit, so it becomes a single window: Available, or Unavailable with Workforce's reason.
    /// </summary>
    public static SchedulingCandidate ToCandidate(TechnicianSchedulingProfile profile, TimeRange visit)
    {
        var window = profile.Availability switch
        {
            TechnicianAvailabilityState.Available => AvailabilityWindow.NewAvailable(visit),
            TechnicianAvailabilityState.Inactive => AvailabilityWindow.NewUnavailable(visit, AvailabilityReason.Inactive),
            TechnicianAvailabilityState.OutsideWorkingHours => AvailabilityWindow.NewUnavailable(visit, AvailabilityReason.OutsideWorkingHours),
            TechnicianAvailabilityState.Absence => AvailabilityWindow.NewUnavailable(visit, AvailabilityReason.Absence),
            TechnicianAvailabilityState.Holiday => AvailabilityWindow.NewUnavailable(visit, AvailabilityReason.Holiday),
            _ => AvailabilityWindow.NewUnavailable(visit, AvailabilityReason.Other)
        };

        return new SchedulingCandidate(
            profile.TechnicianId,
            profile.IsActive,
            ListModule.OfSeq([window]),
            SetModule.OfSeq(profile.SkillCodes));
    }

    public static SchedulingRequirement ToRequirement(TimeRange visit, IEnumerable<string> requiredSkillCodes) =>
        new(visit, SetModule.OfSeq(requiredSkillCodes));

    public static ResourceKind ToKind(ResourceType type) => type switch
    {
        ResourceType.Technician => ResourceKind.Technician,
        ResourceType.Vehicle => ResourceKind.Vehicle,
        ResourceType.Equipment => ResourceKind.Equipment,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    public static ResourceKey ToKey(ResourceType type, Guid resourceId) => new(ToKind(type), resourceId);

    public static ResourceBooking ToBooking(ResourceReservation reservation) =>
        new(
            reservation.Id,
            ToKey(reservation.ResourceType, reservation.ResourceId),
            ToRange(reservation.Start, reservation.End),
            reservation.VisitId is { } visitId ? FSharpOption<Guid>.Some(visitId) : FSharpOption<Guid>.None);

    public static FSharpOption<Guid> ToOption(Guid? value) =>
        value is { } some ? FSharpOption<Guid>.Some(some) : FSharpOption<Guid>.None;
}
