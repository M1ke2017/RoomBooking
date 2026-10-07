using CrewCall.Scheduling.Assignments;
using CrewCall.Scheduling.Checks;
using CrewCall.Scheduling.Core;
using CrewCall.Scheduling.Matching;
using CrewCall.Scheduling.Ports;

namespace CrewCall.Scheduling.Incidents;

/// <summary>
/// The urgent incident workflow (ADR-0012): Analyze → Suggest → Prepare → Dispatch. Analyze and Prepare are advice:
/// they never claim a resource and never change existing work. Dispatch is the commit: in ONE transaction it runs the
/// final check, creates the work order and its visit (WorkOrders, through <see cref="IIncidentWorkOrders"/>), claims the
/// resources exactly as an assignment does (<see cref="AssignmentService"/>, ADR-0009) and marks the incident Dispatched,
/// with every event; on any failure nothing of it remains. The system never moves, cancels or reassigns existing work:
/// a candidate whose plan collides is only reported as RequiresReschedule, for the operator to decide.
/// </summary>
/// <remarks>
/// Atomicity relies on the composition root resolving every module's data interface to one scoped DbContext: the work
/// order and visit staged by WorkOrders and the claim staged here are written by the same SaveChanges and transaction.
/// </remarks>
public sealed class UrgentIncidentService(
    IIncidentWorkOrders incidents,
    ResourceMatchingService matching,
    AssignmentService assignments,
    SchedulingCheckService checks,
    ISchedulingDbContext db,
    TimeProvider clock)
{
    /// <summary>The top direct candidates that get a full scheduling check during analysis.</summary>
    public const int MaxCheckedCandidates = 10;

    /// <summary>At most this many RequiresReschedule, and as many Unavailable, suggestions are returned.</summary>
    public const int MaxSuggestionsPerType = 10;

    public async Task<AnalyzeIncidentOutcome> AnalyzeAsync(AnalyzeIncident command, CancellationToken cancellationToken)
    {
        var incident = await incidents.GetIncidentAsync(command.IncidentId, cancellationToken);
        if (incident is null)
        {
            return new AnalyzeIncidentOutcome.IncidentNotFound(command.IncidentId);
        }

        if (incident.WorkOrderId is not null || incident.State is IncidentState.Dispatched or IncidentState.Resolved or IncidentState.Cancelled)
        {
            return new AnalyzeIncidentOutcome.NotAllowed(incident.IncidentId, incident.State);
        }

        // Matching with the incident's window and skills (Sprint 9, unchanged); its input is validated before any change.
        var matchingRequest = new ResourceMatching(
            incident.RequestedStart, incident.RequestedEnd, incident.RequiredSkillCodes, command.PreferredTeamId,
            command.CandidateTechnicianIds, null, null, null, null, null, null);
        if (ResourceMatchingService.Validate(matchingRequest) is { } invalid)
        {
            return new AnalyzeIncidentOutcome.Invalid(invalid.Errors);
        }

        switch (await incidents.BeginAnalysisAsync(incident.IncidentId, cancellationToken))
        {
            case IncidentStateChange.NotFound:
                return new AnalyzeIncidentOutcome.IncidentNotFound(incident.IncidentId);
            case IncidentStateChange.NotAllowed notAllowed:
                return new AnalyzeIncidentOutcome.NotAllowed(incident.IncidentId, notAllowed.Current);
            case IncidentStateChange.ConcurrentChange:
                return new AnalyzeIncidentOutcome.ConcurrentChange(incident.IncidentId);
        }

        var (failed, evaluation) = await matching.EvaluateAsync(matchingRequest, cancellationToken);
        switch (failed)
        {
            case ResourceMatchingOutcome.Invalid matchingInvalid:
                return new AnalyzeIncidentOutcome.Invalid(matchingInvalid.Errors);
            case ResourceMatchingOutcome.NotFound notFound:
                return new AnalyzeIncidentOutcome.NotFound(notFound.Missing);
        }

        var suggestions = await SuggestAsync(incident, evaluation!, cancellationToken);
        var generatedAt = StoredTime.Normalize(clock.GetUtcNow());
        var eligibleCount = evaluation!.Candidates.Count(candidate => candidate.Feasibility.IsFeasible && candidate.Conflicts.IsEmpty);

        var completed = await incidents.CompleteAnalysisAsync(
            new IncidentAnalysisSummary(
                incident.IncidentId,
                generatedAt,
                evaluation.Candidates.Count,
                eligibleCount,
                evaluation.Candidates.Count - eligibleCount,
                suggestions.Count(s => s.SuggestionType == DispatchSuggestionType.DirectAssignment),
                suggestions.Count(s => s.SuggestionType == DispatchSuggestionType.RequiresReschedule),
                suggestions.Count(s => s.SuggestionType == DispatchSuggestionType.Unavailable)),
            cancellationToken);

        return completed switch
        {
            IncidentStateChange.Changed changed => new AnalyzeIncidentOutcome.Analyzed(new IncidentAnalysis(
                incident.IncidentId,
                changed.State,
                generatedAt,
                evaluation.Candidates.Count,
                evaluation.CandidatesTruncated,
                eligibleCount,
                evaluation.Candidates.Count - eligibleCount,
                suggestions)),
            IncidentStateChange.NotAllowed notAllowed => new AnalyzeIncidentOutcome.NotAllowed(incident.IncidentId, notAllowed.Current),
            IncidentStateChange.ConcurrentChange => new AnalyzeIncidentOutcome.ConcurrentChange(incident.IncidentId),
            _ => new AnalyzeIncidentOutcome.IncidentNotFound(incident.IncidentId)
        };
    }

    /// <summary>
    /// Read-only: would the operator's selection pass dispatch's final check right now, and which existing reservations
    /// does it collide with? Writes nothing; a positive answer is advice, dispatch checks again (CHECK != COMMIT).
    /// </summary>
    public async Task<IncidentDispatchOutcome> PrepareDispatchAsync(IncidentResourceSelection selection, CancellationToken cancellationToken)
    {
        var incident = await incidents.GetIncidentAsync(selection.IncidentId, cancellationToken);
        if (incident is null)
        {
            return new IncidentDispatchOutcome.IncidentNotFound(selection.IncidentId);
        }

        if (NotDispatchable(incident) is { } notDispatchable)
        {
            return notDispatchable;
        }

        var request = ToClaimRequest(selection, incident);
        if (AssignmentService.ValidateClaim(request) is { Any: true } errors)
        {
            return new IncidentDispatchOutcome.Invalid(errors.ToDictionary());
        }

        // The same final check as dispatch, for the visit dispatch would create (a new visit: no own reservations).
        var check = await assignments.FinalCheckAsync(PlannedVisit(incident), request, cancellationToken);
        return check switch
        {
            SchedulingCheckOutcome.Checked checkedOutcome => new IncidentDispatchOutcome.Prepared(new IncidentDispatchReadiness(
                incident.IncidentId,
                checkedOutcome.Result.IsFeasible,
                checkedOutcome.Result,
                await ImpactAsync(checkedOutcome.Result.Reasons, cancellationToken))),
            _ => ToFailure(check)
        };
    }

    /// <summary>
    /// Dispatches the incident with the operator's selection, atomically: final check, work order, visit, assignment,
    /// resource reservations, the incident's link and Dispatched status, and their events, in one transaction. A lost
    /// race (another dispatch of this incident, another claim of the same resources) rolls everything back and is
    /// reported as a conflict.
    /// </summary>
    public async Task<IncidentDispatchOutcome> DispatchIncidentAsync(IncidentResourceSelection selection, CancellationToken cancellationToken)
    {
        if (await incidents.GetIncidentAsync(selection.IncidentId, cancellationToken) is not { } known)
        {
            return new IncidentDispatchOutcome.IncidentNotFound(selection.IncidentId);
        }

        var request = ToClaimRequest(selection, known);
        if (AssignmentService.ValidateClaim(request) is { Any: true } errors)
        {
            return new IncidentDispatchOutcome.Invalid(errors.ToDictionary());
        }

        return await assignments.WithClaimRetriesAsync<IncidentDispatchOutcome>(async (transaction, token) =>
        {
            // 1.-3. The incident, inside the transaction: ready, and not dispatched yet.
            var incident = await incidents.GetIncidentAsync(selection.IncidentId, token);
            if (incident is null)
            {
                return new IncidentDispatchOutcome.IncidentNotFound(selection.IncidentId);
            }

            if (NotDispatchable(incident) is { } notDispatchable)
            {
                return notDispatchable;
            }

            // 4. The final check, again: the analysis and the preparation were advice.
            var visit = PlannedVisit(incident);
            var check = await assignments.FinalCheckAsync(visit, request, token);
            if (check is not SchedulingCheckOutcome.Checked { Result.IsFeasible: true } feasible)
            {
                return check is SchedulingCheckOutcome.Checked rejected
                    ? new IncidentDispatchOutcome.Rejected(rejected.Result.Reasons, await ImpactAsync(rejected.Result.Reasons, token))
                    : ToFailure(check);
            }

            // 5.-6., 9.-10. Work order, visit, the incident's link and status, with their events (staged, not saved).
            var assignmentId = Guid.CreateVersion7();
            var staging = await incidents.StageDispatchAsync(
                new IncidentDispatchPlan(
                    incident.IncidentId, visit.VisitId, assignmentId, feasible.Result.TechnicianId, feasible.Result.VehicleId, feasible.Result.EquipmentIds),
                token);
            if (staging is not IncidentDispatchStaging.Staged staged)
            {
                return staging switch
                {
                    IncidentDispatchStaging.AlreadyDispatched dispatched => new IncidentDispatchOutcome.AlreadyDispatched(incident.IncidentId, dispatched.WorkOrderId),
                    IncidentDispatchStaging.NotReady notReady => new IncidentDispatchOutcome.NotReady(incident.IncidentId, notReady.Current),
                    _ => new IncidentDispatchOutcome.IncidentNotFound(incident.IncidentId)
                };
            }

            // 7.-8. The assignment and its resource reservations: the same claim as every assignment (Sprint 7).
            var assignment = assignments.StageClaim(visit, request, feasible.Result, assignmentId);

            // 11.-12. One SaveChanges, one commit. The database guards decide races: the reservations' exclusion
            // constraint (resources) and the incident's row version (a second dispatch of the same incident).
            try
            {
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            }
            catch (Exception exception) when (AssignmentService.IsRejectedWrite(exception))
            {
                await assignments.RollBackAsync(transaction);

                var current = await incidents.GetIncidentAsync(incident.IncidentId, token);
                if (current is null)
                {
                    return new IncidentDispatchOutcome.IncidentNotFound(incident.IncidentId);
                }

                if (NotDispatchable(current) is { } lostToIncidentChange)
                {
                    return lostToIncidentChange; // dispatched or cancelled by a concurrent request
                }

                if (await assignments.ExplainRejectionAsync(visit, request, token) is { } lostToClaim)
                {
                    return new IncidentDispatchOutcome.Rejected(lostToClaim.Reasons, await ImpactAsync(lostToClaim.Reasons, token));
                }

                return null; // not explainable yet (the winner may not have committed): attempt again
            }

            return new IncidentDispatchOutcome.Dispatched(incident.IncidentId, staged.WorkOrderId, staged.VisitId, assignment);
        }, cancellationToken);
    }

    /// <summary>
    /// The F# dispatch classification of every candidate, then a full scheduling check of the top direct candidates
    /// (a candidate the check rejects is reclassified by its reasons), and the impact of each conflict.
    /// </summary>
    private async Task<List<IncidentDispatchSuggestion>> SuggestAsync(
        IncidentSchedulingInfo incident, MatchingEvaluation evaluation, CancellationToken cancellationToken)
    {
        var direct = new List<Draft>();
        var reschedule = new List<Draft>();
        var unavailable = new List<Draft>();
        var demoted = new List<Draft>();
        var checkedCount = 0;

        foreach (var suggestion in DispatchSuggestions.suggest(ScoringPolicyModule.defaults, evaluation.Candidates))
        {
            var technician = evaluation.Summaries[suggestion.CandidateId];
            var score = suggestion.Score?.Value.TotalScore;

            if (suggestion.Kind.IsDirectAssignment)
            {
                if (checkedCount++ >= MaxCheckedCandidates)
                {
                    continue; // beyond the checked top: counted, not suggested
                }

                var check = await checks.CheckAsync(
                    new SchedulingCheck(
                        null, technician.TechnicianId, null, null, incident.RequestedStart, incident.RequestedEnd,
                        incident.RequiredSkillCodes, null, null),
                    cancellationToken);
                if (check is not SchedulingCheckOutcome.Checked { Result: var result })
                {
                    throw new InvalidOperationException($"The scheduling check of technician '{technician.TechnicianId}' failed: {check}.");
                }

                if (result.IsFeasible)
                {
                    direct.Add(new Draft(technician, DispatchSuggestionType.DirectAssignment, score, true, []));
                }
                else
                {
                    // The plan changed since matching (a concurrent claim): classify by the check's reasons.
                    var infeasible = result.Reasons.Any(reason => IsInfeasibility(reason.Code));
                    demoted.Add(new Draft(
                        technician,
                        infeasible ? DispatchSuggestionType.Unavailable : DispatchSuggestionType.RequiresReschedule,
                        infeasible ? null : score,
                        true,
                        result.Reasons));
                }
            }
            else if (suggestion.Kind.IsRequiresReschedule)
            {
                reschedule.Add(new Draft(
                    technician, DispatchSuggestionType.RequiresReschedule, score, false, evaluation.ReasonsFor(technician.TechnicianId, suggestion.Reasons)));
            }
            else
            {
                unavailable.Add(new Draft(
                    technician, DispatchSuggestionType.Unavailable, null, false, evaluation.ReasonsFor(technician.TechnicianId, suggestion.Reasons)));
            }
        }

        // Demoted candidates were ranked above the others of their new type, so they lead it.
        var ordered = direct
            .Concat(demoted.Where(d => d.Type == DispatchSuggestionType.RequiresReschedule).Concat(reschedule).Take(MaxSuggestionsPerType))
            .Concat(demoted.Where(d => d.Type == DispatchSuggestionType.Unavailable).Concat(unavailable).Take(MaxSuggestionsPerType))
            .ToList();

        var workOrders = await WorkOrdersOfAsync(ordered.SelectMany(draft => draft.Reasons), cancellationToken);
        return ordered
            .Select((draft, index) => new IncidentDispatchSuggestion(
                index + 1,
                draft.Technician.TechnicianId,
                draft.Technician.DisplayName,
                draft.Technician.TeamId,
                draft.Type,
                draft.Score,
                draft.SchedulingChecked,
                draft.Reasons,
                Impact(draft.Reasons, workOrders)))
            .ToList();
    }

    private sealed record Draft(
        TechnicianSummary Technician, DispatchSuggestionType Type, decimal? Score, bool SchedulingChecked, IReadOnlyList<SchedulingConflict> Reasons);

    private static bool IsInfeasibility(SchedulingConflictCode code) =>
        code is SchedulingConflictCode.TechnicianInactive or SchedulingConflictCode.TechnicianUnavailable or SchedulingConflictCode.MissingRequiredSkills;

    private static IncidentDispatchOutcome? NotDispatchable(IncidentSchedulingInfo incident) =>
        incident.WorkOrderId is { } workOrderId ? new IncidentDispatchOutcome.AlreadyDispatched(incident.IncidentId, workOrderId)
        : incident.State != IncidentState.ReadyForDispatch ? new IncidentDispatchOutcome.NotReady(incident.IncidentId, incident.State)
        : null;

    /// <summary>The visit dispatch creates: a new id (so it has no reservations of its own) over the requested window.</summary>
    private static VisitSchedulingInfo PlannedVisit(IncidentSchedulingInfo incident) =>
        new(Guid.CreateVersion7(), incident.RequestedStart, incident.RequestedEnd, VisitState.Planned);

    /// <summary>The operator's selection plus the incident's own required skills.</summary>
    private static ClaimRequest ToClaimRequest(IncidentResourceSelection selection, IncidentSchedulingInfo incident) =>
        new(
            selection.TechnicianId,
            selection.VehicleId,
            selection.EquipmentIds,
            incident.RequiredSkillCodes,
            selection.TravelBufferBeforeMinutes,
            selection.TravelBufferAfterMinutes);

    private static IncidentDispatchOutcome ToFailure(SchedulingCheckOutcome check) => check switch
    {
        SchedulingCheckOutcome.Invalid invalid => new IncidentDispatchOutcome.Invalid(invalid.Errors),
        SchedulingCheckOutcome.ResourcesNotFound notFound => new IncidentDispatchOutcome.ResourcesNotFound(notFound.Missing),
        _ => throw new InvalidOperationException($"Unhandled check outcome {check}.")
    };

    private async Task<IReadOnlyList<DispatchImpact>> ImpactAsync(IReadOnlyList<SchedulingConflict> reasons, CancellationToken cancellationToken) =>
        Impact(reasons, await WorkOrdersOfAsync(reasons, cancellationToken));

    private async Task<IReadOnlyDictionary<Guid, Guid>> WorkOrdersOfAsync(IEnumerable<SchedulingConflict> reasons, CancellationToken cancellationToken)
    {
        var visitIds = reasons.Select(reason => reason.ReservationVisitId).OfType<Guid>().Distinct().ToList();
        return visitIds.Count == 0 ? new Dictionary<Guid, Guid>() : await incidents.GetWorkOrderIdsAsync(visitIds, cancellationToken);
    }

    /// <summary>Every reservation among the reasons, earliest first: what the existing plan holds there. Nothing is changed.</summary>
    private static List<DispatchImpact> Impact(IEnumerable<SchedulingConflict> reasons, IReadOnlyDictionary<Guid, Guid> workOrders) =>
        reasons
            .Where(reason => reason is { ReservationId: not null, ResourceType: not null, RelatedResourceId: not null, ReservedStart: not null, ReservedEnd: not null })
            .Select(reason => new DispatchImpact(
                reason.ReservationId!.Value,
                reason.ResourceType!.Value,
                reason.RelatedResourceId!.Value,
                reason.ReservationVisitId,
                reason.ReservationVisitId is { } visitId && workOrders.TryGetValue(visitId, out var workOrderId) ? workOrderId : null,
                reason.ReservedStart!.Value,
                reason.ReservedEnd!.Value,
                reason.Code == SchedulingConflictCode.TravelBufferConflict))
            .DistinctBy(impact => impact.ReservationId)
            .OrderBy(impact => impact.ReservedStart)
            .ThenBy(impact => impact.ReservationId)
            .ToList();
}
