using CrewCall.Scheduling.Checks;
using CrewCall.Scheduling.Core;
using CrewCall.Scheduling.Ports;
using CrewCall.Scheduling.Reservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.FSharp.Collections;

namespace CrewCall.Scheduling.Matching;

/// <summary>
/// Ranks technicians for a visit (ADR-0011). C# gathers the facts (candidates, Workforce availability and skills, team
/// membership, reservation conflicts, current workload); the pure F# <see cref="Core.Matching"/> decides eligibility and
/// score. Advisory and read-only: it writes nothing and never creates an assignment, which keeps its own final check.
/// </summary>
public sealed class ResourceMatchingService(
    ITechnicianSchedulingSource technicians,
    IResourceCatalog resources,
    ResourceReservationService reservations,
    ISchedulingDbContext db)
{
    public const int MaxCandidates = 200;
    public const int DefaultMaxResults = 10;
    public const int MaxResultsLimit = 50;
    public const int MaxRejectedReturned = 50;

    public async Task<ResourceMatchingOutcome> MatchAsync(ResourceMatching command, CancellationToken cancellationToken)
    {
        if (Validate(command) is { } invalid)
        {
            return invalid;
        }

        var start = StoredTime.Normalize(command.Start!.Value);
        var end = StoredTime.Normalize(command.End!.Value);
        var visit = CoreMapping.ToRange(start, end);
        var (windowStart, windowEnd) = WorkloadWindow(command, start, end);
        var requiredSkills = (command.RequiredSkillCodes ?? []).Where(code => !string.IsNullOrWhiteSpace(code)).ToList();
        var equipmentIds = (command.EquipmentIds ?? []).Distinct().ToList();

        // Candidates and every referenced entity must exist.
        var missing = new List<(string Kind, Guid Id)>();
        IReadOnlyList<TechnicianSummary> candidates;
        var truncated = false;
        if (command.CandidateTechnicianIds is { } requestedIds)
        {
            var ids = requestedIds.Distinct().ToList();
            candidates = await technicians.ListTechniciansAsync(ids, ids.Count, cancellationToken);
            missing.AddRange(ids.Except(candidates.Select(c => c.TechnicianId)).Select(id => ("Technician", id)));
        }
        else
        {
            var discovered = await technicians.ListTechniciansAsync(null, MaxCandidates + 1, cancellationToken);
            truncated = discovered.Count > MaxCandidates;
            candidates = discovered.Take(MaxCandidates).ToList();
        }

        if (command.PreferredTeamId is { } teamId && !await technicians.TeamExistsAsync(teamId, cancellationToken))
        {
            missing.Add(("Team", teamId));
        }

        if (command.VehicleId is { } vehicleId && !await resources.VehicleExistsAsync(vehicleId, cancellationToken))
        {
            missing.Add(("Vehicle", vehicleId));
        }

        if (equipmentIds.Count > 0)
        {
            var missingEquipment = await resources.FindMissingEquipmentAsync(equipmentIds, cancellationToken);
            missing.AddRange(equipmentIds.Where(missingEquipment.Contains).Select(id => ("Equipment", id)));
        }

        if (missing.Count > 0)
        {
            return new ResourceMatchingOutcome.NotFound(missing);
        }

        // Conflicts: one query for every candidate plus the requested vehicle and equipment.
        var requested = candidates.Select(c => (ResourceType.Technician, c.TechnicianId)).ToList();
        if (command.VehicleId is { } requestedVehicle)
        {
            requested.Add((ResourceType.Vehicle, requestedVehicle));
        }

        requested.AddRange(equipmentIds.Select(id => (ResourceType.Equipment, id)));
        var conflicts = await reservations.GetConflictsAsync(requested, visit, visit, command.VisitId, cancellationToken);
        var conflictsByReservation = conflicts.ToDictionary(conflict => conflict.Reservation.Id);
        var sharedConflicts = conflicts.Where(c => c.Reservation.ResourceType != ResourceType.Technician).ToList();
        var technicianConflicts = conflicts
            .Where(c => c.Reservation.ResourceType == ResourceType.Technician)
            .ToLookup(c => c.Reservation.ResourceId);

        // Workload: one query over the active assignments' technician reservations.
        var workloads = await WorkloadsAsync(candidates.Select(c => c.TechnicianId).ToList(), windowStart, windowEnd, command.VisitId, cancellationToken);

        // Availability and skills come from Workforce, one decision per technician.
        var requirement = CoreMapping.ToRequirement(visit, requiredSkills);
        var requiredSet = SetModule.OfSeq(requiredSkills);
        var matchingCandidates = new List<MatchingCandidate>(candidates.Count);
        var summaries = candidates.ToDictionary(c => c.TechnicianId);
        foreach (var candidate in candidates)
        {
            var profile = await technicians.GetProfileAsync(candidate.TechnicianId, start, end, cancellationToken)
                ?? throw new InvalidOperationException($"Technician '{candidate.TechnicianId}' disappeared during matching.");

            var feasibility = Feasibility.evaluateCandidate(CoreMapping.ToCandidate(profile, visit), requirement);
            var candidateConflicts = technicianConflicts[candidate.TechnicianId].Concat(sharedConflicts)
                .Select(conflict => conflict.WithinTravelBuffer
                    ? ResourceConflict.NewTravelBufferOverlap(CoreMapping.ToBooking(conflict.Reservation))
                    : ResourceConflict.NewBookingOverlap(CoreMapping.ToBooking(conflict.Reservation)));

            matchingCandidates.Add(new MatchingCandidate(
                candidate.TechnicianId,
                feasibility,
                ListModule.OfSeq(candidateConflicts),
                Core.Matching.skillCoverage(SetModule.OfSeq(profile.SkillCodes), requiredSet),
                workloads.GetValueOrDefault(candidate.TechnicianId, new CandidateWorkload(0, 0)),
                command.PreferredTeamId is not { } preferred ? PreferredTeamMatch.NoPreference
                    : candidate.TeamId == preferred ? PreferredTeamMatch.InPreferredTeam
                    : PreferredTeamMatch.NotInPreferredTeam));
        }

        var ranked = Core.Matching.rank(ScoringPolicyModule.defaults, matchingCandidates);
        var byId = matchingCandidates.ToDictionary(c => c.CandidateId);

        var eligible = new List<EligibleCandidate>();
        var rejected = new List<RejectedCandidate>();
        foreach (var rankedCandidate in ranked)
        {
            var summary = summaries[rankedCandidate.CandidateId];
            if (rankedCandidate.Result is MatchResult.Eligible { Item: var score })
            {
                var input = byId[rankedCandidate.CandidateId];
                eligible.Add(new EligibleCandidate(
                    eligible.Count + 1,
                    summary.TechnicianId,
                    summary.DisplayName,
                    summary.TeamId,
                    score.TotalScore,
                    new MatchScoreComponents(
                        score.Components.SkillScore, score.Components.AvailabilityScore,
                        score.Components.WorkloadScore, score.Components.TeamPreferenceScore),
                    new MatchWorkload(input.Workload.AssignedMinutesInWindow, input.Workload.AssignmentCountInWindow),
                    new MatchSkillCoverage(input.SkillCoverage.RequiredCount, input.SkillCoverage.MatchedCount, input.SkillCoverage.AdditionalCount),
                    input.PreferredTeamMatch.IsNoPreference ? null : input.PreferredTeamMatch.IsInPreferredTeam));
            }
            else if (rankedCandidate.Result is MatchResult.Rejected { Item: var reasons })
            {
                rejected.Add(new RejectedCandidate(
                    summary.TechnicianId, summary.DisplayName, summary.TeamId, ToReasons(reasons, summary, conflictsByReservation)));
            }
        }

        var maxResults = command.MaxResults ?? DefaultMaxResults;
        return new ResourceMatchingOutcome.Matched(new ResourceMatchingResult(
            start,
            end,
            windowStart,
            windowEnd,
            candidates.Count,
            truncated,
            eligible.Count,
            rejected.Count,
            eligible.Take(maxResults).ToList(),
            rejected.Take(MaxRejectedReturned).ToList()));
    }

    private static ResourceMatchingOutcome.Invalid? Validate(ResourceMatching command)
    {
        var errors = new ValidationErrors();

        if (command.Start is null)
        {
            errors.Add("start", "Required.");
        }

        if (command.End is null)
        {
            errors.Add("end", "Required.");
        }

        if (command.Start is { } requestedStart && command.End is { } requestedEnd)
        {
            var start = StoredTime.Normalize(requestedStart);
            var end = StoredTime.Normalize(requestedEnd);
            if (start >= end)
            {
                errors.Add("end", "Must be after start.");
            }
            else if (end - start > SchedulingCheckService.MaxVisitLength)
            {
                errors.Add("end", $"The range can be at most {SchedulingCheckService.MaxVisitLength.TotalDays:0} days long.");
            }
        }

        if (command.MaxResults is < 1 or > MaxResultsLimit)
        {
            errors.Add("maxResults", $"Must be between 1 and {MaxResultsLimit}.");
        }

        if (command.CandidateTechnicianIds is { } candidateIds)
        {
            if (candidateIds.Count == 0 || candidateIds.Contains(Guid.Empty))
            {
                errors.Add("candidateTechnicianIds", "Must contain at least one id and no empty ids (omit it to rank all active technicians).");
            }
            else if (candidateIds.Distinct().Count() > MaxCandidates)
            {
                errors.Add("candidateTechnicianIds", $"At most {MaxCandidates} technicians.");
            }
        }

        if (command.PreferredTeamId == Guid.Empty)
        {
            errors.Add("preferredTeamId", "Must not be empty when provided.");
        }

        if (command.VehicleId == Guid.Empty)
        {
            errors.Add("vehicleId", "Must not be empty when provided.");
        }

        if (command.VisitId == Guid.Empty)
        {
            errors.Add("visitId", "Must not be empty when provided.");
        }

        if (command.EquipmentIds is { } equipmentIds && (equipmentIds.Contains(Guid.Empty) || equipmentIds.Distinct().Count() > SchedulingCheckService.MaxEquipmentItems))
        {
            errors.Add("equipmentIds", $"At most {SchedulingCheckService.MaxEquipmentItems} non-empty ids.");
        }

        if ((command.RequiredSkillCodes?.Count ?? 0) > SchedulingCheckService.MaxRequiredSkills)
        {
            errors.Add("requiredSkillCodes", $"At most {SchedulingCheckService.MaxRequiredSkills} codes.");
        }

        if ((command.WorkloadWindowStart is null) != (command.WorkloadWindowEnd is null))
        {
            errors.Add("workloadWindowEnd", "Provide both workloadWindowStart and workloadWindowEnd, or neither.");
        }
        else if (command.WorkloadWindowStart is { } windowStart && command.WorkloadWindowEnd is { } windowEnd)
        {
            if (windowStart >= windowEnd)
            {
                errors.Add("workloadWindowEnd", "Must be after workloadWindowStart.");
            }
            else if (windowEnd - windowStart > SchedulingCheckService.MaxVisitLength)
            {
                errors.Add("workloadWindowEnd", $"The workload window can be at most {SchedulingCheckService.MaxVisitLength.TotalDays:0} days long.");
            }
        }

        return errors.Any ? new ResourceMatchingOutcome.Invalid(errors.ToDictionary()) : null;
    }

    /// <summary>The requested window, or by default the whole UTC days the request touches.</summary>
    private static (DateTimeOffset Start, DateTimeOffset End) WorkloadWindow(ResourceMatching command, DateTimeOffset start, DateTimeOffset end)
    {
        if (command.WorkloadWindowStart is { } windowStart && command.WorkloadWindowEnd is { } windowEnd)
        {
            return (StoredTime.Normalize(windowStart), StoredTime.Normalize(windowEnd));
        }

        var firstDay = new DateTimeOffset(start.UtcDateTime.Date, TimeSpan.Zero);
        var lastDay = new DateTimeOffset(end.AddTicks(-1).UtcDateTime.Date, TimeSpan.Zero);
        return (firstDay, lastDay.AddDays(1));
    }

    /// <summary>
    /// Minutes and number of the technicians' active assignments in the window, from the assignments' technician
    /// reservations (the claimed window, travel buffers included). Replaced and cancelled assignments hold no reservations.
    /// </summary>
    private async Task<Dictionary<Guid, CandidateWorkload>> WorkloadsAsync(
        List<Guid> technicianIds, DateTimeOffset windowStart, DateTimeOffset windowEnd, Guid? ignoredVisitId, CancellationToken cancellationToken)
    {
        if (technicianIds.Count == 0)
        {
            return [];
        }

        var query = db.ResourceReservations
            .AsNoTracking()
            .Where(reservation => reservation.ResourceType == ResourceType.Technician
                && reservation.AssignmentId != null
                && technicianIds.Contains(reservation.ResourceId)
                && reservation.Start < windowEnd && windowStart < reservation.End);

        if (ignoredVisitId is { } visitId)
        {
            query = query.Where(reservation => reservation.VisitId != visitId);
        }

        var rows = await query
            .Select(reservation => new { reservation.ResourceId, reservation.AssignmentId, reservation.Start, reservation.End })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.ResourceId)
            .ToDictionary(
                group => group.Key,
                group => new CandidateWorkload(
                    (int)Math.Floor(group.Sum(row =>
                        ((row.End < windowEnd ? row.End : windowEnd) - (row.Start > windowStart ? row.Start : windowStart)).TotalMinutes)),
                    group.Select(row => row.AssignmentId).Distinct().Count()));
    }

    private static List<SchedulingConflict> ToReasons(
        FSharpList<MatchRejectionReason> reasons,
        TechnicianSummary technician,
        Dictionary<Guid, ReservationConflict> conflictsByReservation)
    {
        var infeasible = reasons.OfType<MatchRejectionReason.Infeasible>().Select(reason => reason.Item).ToList();
        var result = new List<SchedulingConflict>();

        if (infeasible.Count > 0)
        {
            // Workforce gives the detail of an unavailability; matching keeps only the reasons (one decision per technician).
            var profile = new TechnicianSchedulingProfile(technician.TechnicianId, technician.IsActive, [], TechnicianAvailabilityState.Available, null);
            result.AddRange(SchedulingCheckService.FeasibilityReasons(
                FeasibilityResult.NewRejected(ListModule.OfSeq(infeasible)), profile));
        }

        foreach (var conflict in reasons.OfType<MatchRejectionReason.Conflict>())
        {
            var booking = conflict.Item switch
            {
                ResourceConflict.BookingOverlap overlap => overlap.Item,
                ResourceConflict.TravelBufferOverlap buffer => buffer.Item,
                _ => throw new InvalidOperationException($"Unhandled conflict {conflict.Item}.")
            };
            result.Add(SchedulingCheckService.ToReason(conflictsByReservation[booking.BookingId]));
        }

        return result;
    }
}
