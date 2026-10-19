using CrewCall.Scheduling.Core;
using CrewCall.Scheduling.Ports;
using CrewCall.Scheduling.Reservations;

namespace CrewCall.Scheduling.Checks;

/// <summary>
/// The read-only scheduling check: validates the request, consumes Workforce's technician profile and availability,
/// asks the F# core whether the technician is feasible, looks up clashing resource reservations, and returns every
/// reason together (it never stops at the first). It writes nothing, so a feasible answer is advice, not a claim
/// (ADR-0008: CHECK != COMMIT).
/// </summary>
public sealed class SchedulingCheckService(
    ITechnicianSchedulingSource technicians, IResourceCatalog resources, ResourceReservationService reservations)
{
    public const int MaxTravelBufferMinutes = 480;
    public const int MaxEquipmentItems = 50;
    public const int MaxRequiredSkills = 50;

    /// <summary>The longest visit that can be checked; matches the longest interval Workforce availability evaluates.</summary>
    public static readonly TimeSpan MaxVisitLength = TimeSpan.FromDays(31);

    /// <summary>Candidate start times of a slot search are this far apart (quarter hours).</summary>
    public static readonly TimeSpan SlotStep = TimeSpan.FromMinutes(15);

    public async Task<SchedulingCheckOutcome> CheckAsync(SchedulingCheck command, CancellationToken cancellationToken)
    {
        // 1. Validate.
        var errors = new ValidationErrors();
        var technicianId = command.TechnicianId ?? Guid.Empty;
        if (technicianId == Guid.Empty)
        {
            errors.Add("technicianId", "Required.");
        }

        if (command.VehicleId == Guid.Empty)
        {
            errors.Add("vehicleId", "Must not be empty when provided.");
        }

        if (command.VisitId == Guid.Empty)
        {
            errors.Add("visitId", "Must not be empty when provided.");
        }

        var equipmentIds = (command.EquipmentIds ?? []).Distinct().ToList();
        if (equipmentIds.Contains(Guid.Empty))
        {
            errors.Add("equipmentIds", "Must not contain empty ids.");
        }
        else if (equipmentIds.Count > MaxEquipmentItems)
        {
            errors.Add("equipmentIds", $"At most {MaxEquipmentItems} items.");
        }

        var requiredSkills = (command.RequiredSkillCodes ?? []).Where(code => !string.IsNullOrWhiteSpace(code)).ToList();
        if (requiredSkills.Count > MaxRequiredSkills)
        {
            errors.Add("requiredSkillCodes", $"At most {MaxRequiredSkills} codes.");
        }

        var before = Buffer(errors, "travelBufferBeforeMinutes", command.TravelBufferBeforeMinutes);
        var after = Buffer(errors, "travelBufferAfterMinutes", command.TravelBufferAfterMinutes);

        if (command.Start is null)
        {
            errors.Add("start", "Required.");
        }

        if (command.End is null)
        {
            errors.Add("end", "Required.");
        }

        DateTimeOffset? start = command.Start is { } requestedStart ? StoredTime.Normalize(requestedStart) : null;
        DateTimeOffset? end = command.End is { } requestedEnd ? StoredTime.Normalize(requestedEnd) : null;

        TimeRange? visit = null;
        TimeRange? effective = null;
        if (start is not null && end is not null)
        {
            if (start >= end)
            {
                errors.Add("end", "Must be after start.");
            }
            else if (end - start > MaxVisitLength)
            {
                errors.Add("end", $"The visit can be at most {MaxVisitLength.TotalDays:0} days long.");
            }
            else
            {
                visit = CoreMapping.ToRange(start.Value, end.Value);
                var buffered = TravelBufferModule.effectiveRange(new TravelBuffer(before, after), visit);
                if (buffered.IsOk)
                {
                    effective = buffered.ResultValue;
                }
                else if (!errors.Any)
                {
                    errors.Add("travelBuffer", "The visit plus its travel buffer falls outside the supported date range.");
                }
            }
        }

        if (errors.Any || visit is null || effective is null)
        {
            return new SchedulingCheckOutcome.Invalid(errors.ToDictionary());
        }

        // 2.-4. Load the technician (skills and Workforce availability) and confirm every resource exists.
        var profile = await technicians.GetProfileAsync(technicianId, start!.Value, end!.Value, cancellationToken);
        var missing = new List<(ResourceType, Guid)>();
        if (profile is null)
        {
            missing.Add((ResourceType.Technician, technicianId));
        }

        if (command.VehicleId is { } vehicleId && !await resources.VehicleExistsAsync(vehicleId, cancellationToken))
        {
            missing.Add((ResourceType.Vehicle, vehicleId));
        }

        if (equipmentIds.Count > 0)
        {
            var missingEquipment = await resources.FindMissingEquipmentAsync(equipmentIds, cancellationToken);
            missing.AddRange(equipmentIds.Where(missingEquipment.Contains).Select(id => (ResourceType.Equipment, id)));
        }

        if (missing.Count > 0)
        {
            return new SchedulingCheckOutcome.ResourcesNotFound(missing);
        }

        // 5.-7. The F# feasibility decision for the technician.
        var reasons = new List<SchedulingConflict>();
        var feasibility = Feasibility.evaluateCandidate(
            CoreMapping.ToCandidate(profile!, visit),
            CoreMapping.ToRequirement(visit, requiredSkills));
        reasons.AddRange(FeasibilityReasons(feasibility, profile!));

        // 8.-11. Reservation conflicts for every requested resource, against the visit plus its travel buffer.
        var requested = new List<(ResourceType Type, Guid ResourceId)> { (ResourceType.Technician, technicianId) };
        if (command.VehicleId is { } requestedVehicle)
        {
            requested.Add((ResourceType.Vehicle, requestedVehicle));
        }

        requested.AddRange(equipmentIds.Select(id => (ResourceType.Equipment, id)));

        var conflicts = await reservations.GetConflictsAsync(requested, visit, effective, command.VisitId, cancellationToken);
        reasons.AddRange(conflicts.Select(ToReason));

        // 12.-13. Everything together; feasible only without any reason.
        return new SchedulingCheckOutcome.Checked(new SchedulingCheckResult(
            technicianId,
            command.VehicleId,
            equipmentIds,
            command.VisitId,
            start.Value,
            end.Value,
            effective.Start,
            effective.End,
            reasons));
    }

    /// <summary>
    /// FindNextAvailableSlot (Sprint 15): the first window, scanning forward in quarter hours from
    /// <see cref="SlotSearch.NotBefore"/>, where this check passes for the visit's own resources: working hours, absences
    /// and holidays (Workforce availability), and the technician's, vehicle's and equipment's reservations including the
    /// travel buffers. The visit's own reservations are ignored, as for any re-plan. Read-only; null when nothing is free
    /// before <see cref="SlotSearch.NotAfter"/>. Not an optimizer: the first feasible slot wins.
    /// </summary>
    public async Task<SchedulingCheckResult?> FindNextAvailableSlotAsync(SlotSearch search, CancellationToken cancellationToken)
    {
        var start = AlignToSlot(search.NotBefore);
        while (start + search.Duration <= search.NotAfter)
        {
            var outcome = await CheckAsync(
                new SchedulingCheck(
                    search.VisitId, search.TechnicianId, search.VehicleId, search.EquipmentIds, start, start + search.Duration, null,
                    search.TravelBufferBeforeMinutes, search.TravelBufferAfterMinutes),
                cancellationToken);
            if (outcome is not SchedulingCheckOutcome.Checked { Result: var result })
            {
                return null;
            }

            if (result.IsFeasible)
            {
                return result;
            }

            // Jump past the reservations in the way: the visit (with its buffer before) has to start after them. Anything
            // else (working hours, an absence) is stepped over a quarter hour at a time.
            var next = start + SlotStep;
            var reservedUntil = result.Reasons.Select(reason => reason.ReservedEnd).OfType<DateTimeOffset>().ToList();
            if (reservedUntil.Count > 0)
            {
                var afterReservations = AlignToSlot(reservedUntil.Max() + TimeSpan.FromMinutes(search.TravelBufferBeforeMinutes));
                next = afterReservations > next ? afterReservations : next;
            }

            start = next;
        }

        return null;
    }

    private static DateTimeOffset AlignToSlot(DateTimeOffset instant)
    {
        var ticks = instant.UtcTicks;
        var step = SlotStep.Ticks;
        return new DateTimeOffset(ticks % step == 0 ? ticks : ticks - ticks % step + step, TimeSpan.Zero);
    }

    private static TimeSpan Buffer(ValidationErrors errors, string field, int? minutes)
    {
        var value = minutes ?? 0;
        if (value is < 0 or > MaxTravelBufferMinutes)
        {
            errors.Add(field, $"Must be between 0 and {MaxTravelBufferMinutes} minutes.");
            return TimeSpan.Zero;
        }

        return TimeSpan.FromMinutes(value);
    }

    internal static IEnumerable<SchedulingConflict> FeasibilityReasons(FeasibilityResult feasibility, TechnicianSchedulingProfile profile)
    {
        if (feasibility is not FeasibilityResult.Rejected rejected)
        {
            yield break;
        }

        var inactive = rejected.Item.Any(reason => reason.IsCandidateInactive);

        foreach (var reason in rejected.Item)
        {
            switch (reason)
            {
                case { IsCandidateInactive: true }:
                    yield return Reason(SchedulingConflictCode.TechnicianInactive, "The technician is inactive.", profile.TechnicianId, []);
                    break;

                case RejectionReason.MissingRequiredSkills missingSkills:
                    var codes = missingSkills.Item.ToList();
                    yield return Reason(
                        SchedulingConflictCode.MissingRequiredSkills,
                        $"The technician lacks the required skills: {string.Join(", ", codes)}.",
                        profile.TechnicianId,
                        codes);
                    break;

                case RejectionReason.OutsideAvailability outside:
                    var availabilityReasons = outside.Item.Select(item => item.ToString()).ToList();

                    // Workforce stops at "inactive" without looking further; TechnicianInactive already says that.
                    if (inactive && availabilityReasons is ["Inactive"])
                    {
                        break;
                    }

                    var details = profile.AvailabilityDetail is { } detail ? [.. availabilityReasons, detail] : availabilityReasons;
                    yield return Reason(
                        SchedulingConflictCode.TechnicianUnavailable,
                        $"Workforce reports the technician unavailable: {string.Join(", ", availabilityReasons)}.",
                        profile.TechnicianId,
                        details);
                    break;
            }
        }
    }

    private static SchedulingConflict Reason(SchedulingConflictCode code, string message, Guid technicianId, IReadOnlyList<string> details) =>
        new(code, message, ResourceType.Technician, technicianId, null, null, null, details);

    internal static SchedulingConflict ToReason(ReservationConflict conflict)
    {
        var reservation = conflict.Reservation;
        var (code, message) = conflict.WithinTravelBuffer
            ? (SchedulingConflictCode.TravelBufferConflict,
               $"{reservation.ResourceType} '{reservation.ResourceId}' is reserved within the travel buffer around the visit.")
            : reservation.ResourceType switch
            {
                ResourceType.Technician => (SchedulingConflictCode.TechnicianReservationConflict,
                    $"Technician '{reservation.ResourceId}' is already reserved during the visit."),
                ResourceType.Vehicle => (SchedulingConflictCode.VehicleReservationConflict,
                    $"Vehicle '{reservation.ResourceId}' is already reserved during the visit."),
                _ => (SchedulingConflictCode.EquipmentReservationConflict,
                    $"Equipment '{reservation.ResourceId}' is already reserved during the visit.")
            };

        return new SchedulingConflict(
            code, message, reservation.ResourceType, reservation.ResourceId, reservation.Id, reservation.Start, reservation.End, [], reservation.VisitId);
    }
}
