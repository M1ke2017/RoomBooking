using CrewCall.Contracts.Incidents;
using CrewCall.Scheduling.Assignments;
using CrewCall.Scheduling.Checks;
using CrewCall.Scheduling.Core;
using CrewCall.Scheduling.Matching;
using CrewCall.Scheduling.Ports;
using CrewCall.Scheduling.Reservations;

namespace CrewCall.Scheduling.Incidents;

/// <summary>
/// Dynamic rescheduling (Sprint 15, ADR-0017): when the best technician for an urgent incident is busy, propose moving the
/// ONE lower-priority visit in the way to its next free slot; the manager decides; apply does it atomically.
/// Propose is advice (nothing is written, like analyze and prepare). Apply trusts nothing from the proposal: it checks
/// the moved visit's new slot and the urgent claim again, inside its transaction.
/// </summary>
public sealed partial class UrgentIncidentService
{
    /// <summary>At most this many proposals are returned.</summary>
    public const int MaxRescheduleProposals = 5;

    /// <summary>The best-ranked candidates (by matching) that get a proposal worked out.</summary>
    public const int MaxRescheduleCandidates = 10;

    /// <summary>How far ahead a displaced visit's new slot is searched.</summary>
    public static readonly TimeSpan RescheduleHorizon = TimeSpan.FromDays(7);

    /// <summary>
    /// Proposals for dispatching the incident, the smallest business impact first: no move, then the shortest delay,
    /// then the fewest visits affected. Candidates come from resource matching (skills, availability, workload); one the
    /// full check finds inactive, unqualified or unavailable is not proposed. Read-only.
    /// </summary>
    public async Task<IncidentDispatchOutcome> ProposeRescheduleAsync(
        Guid incidentId, RescheduleProposalRequest request, CancellationToken cancellationToken)
    {
        var incident = await incidents.GetIncidentAsync(incidentId, cancellationToken);
        if (incident is null)
        {
            return new IncidentDispatchOutcome.IncidentNotFound(incidentId);
        }

        if (NotDispatchable(incident) is { } notDispatchable)
        {
            return notDispatchable;
        }

        var selection = new IncidentResourceSelection(
            incidentId, null, request.VehicleId, request.EquipmentIds, request.TravelBufferBeforeMinutes, request.TravelBufferAfterMinutes);
        if (AssignmentService.ValidateClaim(ToClaimRequest(selection, incident)) is { Any: true } errors)
        {
            return new IncidentDispatchOutcome.Invalid(errors.ToDictionary());
        }

        var matchingRequest = new ResourceMatching(
            incident.RequestedStart, incident.RequestedEnd, incident.RequiredSkillCodes, null, request.CandidateTechnicianIds,
            null, null, null, null, null, null);
        if (ResourceMatchingService.Validate(matchingRequest) is { } invalid)
        {
            return new IncidentDispatchOutcome.Invalid(invalid.Errors);
        }

        var (failed, evaluation) = await matching.EvaluateAsync(matchingRequest, cancellationToken);
        switch (failed)
        {
            case ResourceMatchingOutcome.Invalid matchingInvalid:
                return new IncidentDispatchOutcome.Invalid(matchingInvalid.Errors);
            case ResourceMatchingOutcome.NotFound notFound:
                return new IncidentDispatchOutcome.ResourcesNotFound(
                    notFound.Missing.Where(m => m.Kind == "Technician").Select(m => (ResourceType.Technician, m.Id)).ToList());
        }

        var proposals = new List<(RescheduleProposal Proposal, int CandidateOrder)>();
        foreach (var suggestion in DispatchSuggestions.suggest(ScoringPolicyModule.defaults, evaluation!.Candidates))
        {
            if (!suggestion.Kind.IsDirectAssignment && !suggestion.Kind.IsRequiresReschedule)
            {
                continue; // inactive, missing skills or unavailable: never proposed
            }

            if (proposals.Count >= MaxRescheduleCandidates)
            {
                break;
            }

            var technician = evaluation.Summaries[suggestion.CandidateId];
            var candidate = selection with { TechnicianId = technician.TechnicianId };
            switch (await ProposeForAsync(incident, candidate, technician.DisplayName, cancellationToken))
            {
                case { Failure: { } failure }:
                    return failure;
                case { Proposal: { } proposal }:
                    proposals.Add((proposal, proposals.Count));
                    break;
            }
        }

        var ranked = proposals
            .OrderByDescending(p => p.Proposal.IsFeasible)
            .ThenBy(p => p.Proposal.Impact.AffectedVisitCount > 0)
            .ThenBy(p => p.Proposal.Impact.DelayMinutes)
            .ThenBy(p => p.Proposal.Impact.AffectedVisitCount)
            .ThenBy(p => p.CandidateOrder)
            .Take(MaxRescheduleProposals)
            .Select((p, index) => p.Proposal with { Rank = index + 1 })
            .ToList();
        return new IncidentDispatchOutcome.Proposed(ranked);
    }

    /// <summary>
    /// The manager accepts a proposal. In ONE transaction: the moved visit's new slot is checked again for its own
    /// resources, the visit moves (same visit, same assignment, its reservations moved), then the urgent claim is checked
    /// again and the incident is dispatched exactly as <see cref="DispatchIncidentAsync"/> does. If anything changed since
    /// the proposal, nothing is written and the outcome says why (HTTP 409).
    /// </summary>
    public async Task<IncidentDispatchOutcome> ApplyRescheduleAsync(
        Guid incidentId, ApplyRescheduleRequest request, CancellationToken cancellationToken)
    {
        var selection = new IncidentResourceSelection(
            incidentId, request.TechnicianId, request.VehicleId, request.EquipmentIds, request.TravelBufferBeforeMinutes, request.TravelBufferAfterMinutes);

        if (request.MovedVisitId is not { } movedVisitId)
        {
            // A proposal without a move is a plain dispatch.
            return request.MovedVisitNewStartUtc is null && request.MovedVisitNewEndUtc is null
                ? await DispatchIncidentAsync(selection, cancellationToken)
                : Invalid("movedVisitId", "Required with a new window for the moved visit.");
        }

        if (ValidateMove(movedVisitId, request) is { } invalidMove)
        {
            return invalidMove;
        }

        var (newStart, newEnd) = (StoredTime.Normalize(request.MovedVisitNewStartUtc!.Value), StoredTime.Normalize(request.MovedVisitNewEndUtc!.Value));
        if (await incidents.GetIncidentAsync(incidentId, cancellationToken) is not { } known)
        {
            return new IncidentDispatchOutcome.IncidentNotFound(incidentId);
        }

        var urgentClaim = ToClaimRequest(selection, known);
        if (AssignmentService.ValidateClaim(urgentClaim) is { Any: true } errors)
        {
            return new IncidentDispatchOutcome.Invalid(errors.ToDictionary());
        }

        return await assignments.WithClaimRetriesAsync<IncidentDispatchOutcome>(async (transaction, token) =>
        {
            // 1. The incident and the visit to move, as they are now: the proposal may be stale.
            var incident = await incidents.GetIncidentAsync(incidentId, token);
            if (incident is null)
            {
                return new IncidentDispatchOutcome.IncidentNotFound(incidentId);
            }

            if (NotDispatchable(incident) is { } notDispatchable)
            {
                return notDispatchable;
            }

            var plan = (await incidents.GetVisitPlansAsync([movedVisitId], token)).GetValueOrDefault(movedVisitId);
            if (MoveNotAllowed(incident, plan, movedVisitId) is { } notAllowed)
            {
                return new IncidentDispatchOutcome.Stale(notAllowed);
            }

            if (newEnd - newStart != plan!.End - plan.Start)
            {
                return Invalid("movedVisitNewEndUtc", "The moved visit keeps its duration.");
            }

            var active = await assignments.FindActiveAsync(movedVisitId, tracked: true, token);
            if (active is null)
            {
                return new IncidentDispatchOutcome.Stale($"Visit '{movedVisitId}' has no active assignment any more.");
            }

            // 2. Its new slot, for its own resources (its current reservations do not count against it).
            var moved = new VisitSchedulingInfo(movedVisitId, newStart, newEnd, VisitState.Planned);
            var movedCheck = await assignments.FinalCheckAsync(moved, AssignmentService.ClaimOf(active), token);
            if (movedCheck is not SchedulingCheckOutcome.Checked { Result.IsFeasible: true } movedFeasible)
            {
                return movedCheck is SchedulingCheckOutcome.Checked blocked
                    ? new IncidentDispatchOutcome.Rejected(blocked.Result.Reasons, await ImpactAsync(blocked.Result.Reasons, token))
                    : ToFailure(movedCheck);
            }

            var urgentVisit = PlannedVisit(incident);
            try
            {
                // 3. Move the visit: same visit, same assignment, its reservations take the new window. Saved (not
                // committed), so the urgent check below sees the plan as it will be.
                var staged = await incidents.StageVisitRescheduleAsync(
                    new VisitReschedulePlan(movedVisitId, newStart, newEnd, incident.IncidentId, urgentVisit.VisitId, request.TechnicianId!.Value), token);
                if (!staged)
                {
                    await assignments.RollBackAsync(transaction);
                    return new IncidentDispatchOutcome.Stale($"Visit '{movedVisitId}' can no longer be moved.");
                }

                await assignments.StageMoveAsync(active, movedFeasible.Result, token);
                await db.SaveChangesAsync(token);

                // 4. The urgent claim, checked again with the visit out of the way, then the dispatch as usual.
                var check = await assignments.FinalCheckAsync(urgentVisit, urgentClaim, token);
                if (check is not SchedulingCheckOutcome.Checked { Result.IsFeasible: true } feasible)
                {
                    await assignments.RollBackAsync(transaction);
                    return check is SchedulingCheckOutcome.Checked rejected
                        ? new IncidentDispatchOutcome.Rejected(rejected.Result.Reasons, await ImpactAsync(rejected.Result.Reasons, token))
                        : ToFailure(check);
                }

                var assignmentId = Guid.CreateVersion7();
                var dispatch = await incidents.StageDispatchAsync(
                    new IncidentDispatchPlan(
                        incident.IncidentId, urgentVisit.VisitId, assignmentId, feasible.Result.TechnicianId, feasible.Result.VehicleId,
                        feasible.Result.EquipmentIds),
                    token);
                if (dispatch is not IncidentDispatchStaging.Staged dispatched)
                {
                    await assignments.RollBackAsync(transaction);
                    return dispatch is IncidentDispatchStaging.AlreadyDispatched already
                        ? new IncidentDispatchOutcome.AlreadyDispatched(incident.IncidentId, already.WorkOrderId)
                        : new IncidentDispatchOutcome.Stale($"Incident '{incident.IncidentId}' can no longer be dispatched.");
                }

                var assignment = assignments.StageClaim(urgentVisit, urgentClaim, feasible.Result, assignmentId);

                // 5. One commit for all of it. The guards decide races: the incident's, visit's and assignment's row
                // versions and the reservations' exclusion constraint.
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);

                return new IncidentDispatchOutcome.Rescheduled(
                    incident.IncidentId, dispatched.WorkOrderId, dispatched.VisitId, assignment,
                    Affected(plan, newStart, newEnd));
            }
            catch (Exception exception) when (AssignmentService.IsRejectedWrite(exception))
            {
                await assignments.RollBackAsync(transaction);

                // Lost a race: report what changed (another apply or dispatch, a moved or started visit, a claim).
                if (await incidents.GetIncidentAsync(incident.IncidentId, token) is { } current && NotDispatchable(current) is { } lost)
                {
                    return lost;
                }

                var now = (await incidents.GetVisitPlansAsync([movedVisitId], token)).GetValueOrDefault(movedVisitId);
                if (now is null || now.Start != plan.Start || now.State != VisitState.Planned)
                {
                    return new IncidentDispatchOutcome.Stale($"Visit '{movedVisitId}' was changed by another request.");
                }

                return null; // not explainable yet (the winner may not have committed): attempt again
            }
        }, cancellationToken);
    }

    private sealed record CandidateProposal(RescheduleProposal? Proposal, IncidentDispatchOutcome? Failure);

    /// <summary>
    /// One technician's proposal: direct when the full check passes; otherwise, when only reservations of ONE planned,
    /// lower-priority visit are in the way, that visit moved to its first free slot after the urgent visit; otherwise an
    /// infeasible proposal saying why. Null (no proposal) for a technician the check finds inactive, unqualified or
    /// unavailable.
    /// </summary>
    private async Task<CandidateProposal?> ProposeForAsync(
        IncidentSchedulingInfo incident, IncidentResourceSelection selection, string technicianName, CancellationToken cancellationToken)
    {
        var claim = ToClaimRequest(selection, incident);
        var directCheck = await assignments.FinalCheckAsync(PlannedVisit(incident), claim, cancellationToken);
        if (directCheck is not SchedulingCheckOutcome.Checked { Result: var direct })
        {
            return new CandidateProposal(null, ToFailure(directCheck)); // e.g. the selected vehicle does not exist
        }

        RescheduleProposal proposal(VisitPlanInfo? plan, Assignment? active, SchedulingCheckResult? slot) =>
            Proposal(incident, selection, technicianName, direct, plan, active, slot);

        if (direct.IsFeasible)
        {
            return new CandidateProposal(proposal(null, null, null), null);
        }

        if (direct.Reasons.Any(reason => IsInfeasibility(reason.Code)))
        {
            return null;
        }

        var blocking = direct.Reasons.Select(reason => reason.ReservationVisitId).Distinct().ToList();
        if (blocking.Contains(null))
        {
            return Infeasible(proposal(null, null, null), "A reservation that belongs to no visit is in the way; it cannot be moved.");
        }

        if (blocking.Count > 1)
        {
            return Infeasible(proposal(null, null, null), $"{blocking.Count} visits are in the way; a proposal moves at most one.");
        }

        var movedVisitId = blocking[0]!.Value;
        var plan = (await incidents.GetVisitPlansAsync([movedVisitId], cancellationToken)).GetValueOrDefault(movedVisitId);
        if (MoveNotAllowed(incident, plan, movedVisitId) is { } notAllowed)
        {
            return Infeasible(proposal(plan, null, null), notAllowed);
        }

        var active = await assignments.FindActiveAsync(movedVisitId, tracked: false, cancellationToken);
        if (active is null)
        {
            return Infeasible(proposal(plan, null, null), $"Visit '{movedVisitId}' has no active assignment to move.");
        }

        // The urgent visit with the moved visit's reservations out of the way: is anything else blocking?
        var withoutMoved = await assignments.FinalCheckAsync(
            new VisitSchedulingInfo(movedVisitId, incident.RequestedStart, incident.RequestedEnd, VisitState.Planned), claim, cancellationToken);
        if (withoutMoved is not SchedulingCheckOutcome.Checked { Result.IsFeasible: true } urgent)
        {
            var reasons = withoutMoved is SchedulingCheckOutcome.Checked stillBlocked ? stillBlocked.Result.Reasons.Select(r => r.Message) : [];
            return Infeasible(proposal(plan, active, null), "Even with the visit moved: " + string.Join(" ", reasons));
        }

        // Its next free slot, after the urgent visit and its travel buffer (so they never meet), within the horizon.
        var notBefore = Latest(
            urgent.Result.EffectiveEnd + TimeSpan.FromMinutes(active.TravelBufferBeforeMinutes),
            plan!.Start,
            StoredTime.Normalize(clock.GetUtcNow()));
        var slot = await checks.FindNextAvailableSlotAsync(
            new SlotSearch(
                movedVisitId, active.TechnicianId, active.VehicleId, active.Equipment.Select(e => e.EquipmentId).ToList(),
                active.TravelBufferBeforeMinutes, active.TravelBufferAfterMinutes, plan.End - plan.Start, notBefore, notBefore + RescheduleHorizon),
            cancellationToken);
        if (slot is null)
        {
            return Infeasible(proposal(plan, active, null), $"Visit '{movedVisitId}' has no free slot within {RescheduleHorizon.TotalDays:0} days.");
        }

        var affected = Affected(plan, slot.Start, slot.End);
        var warnings = new List<string>
        {
            $"Customer '{plan.CustomerId}' (site '{plan.SiteId}') must be told: visit '{movedVisitId}' moves by {affected.DelayMinutes} minutes."
        };
        if (slot.Start.UtcDateTime.Date != plan.Start.UtcDateTime.Date)
        {
            warnings.Add($"Visit '{movedVisitId}' moves to another day ({slot.Start:yyyy-MM-dd}).");
        }

        return new CandidateProposal(
            proposal(plan, active, slot) with { Impact = new RescheduleImpact(1, 1, affected.DelayMinutes, true, warnings, [affected]) },
            null);
    }

    private static readonly RescheduleImpact NoImpact = new(0, 0, 0, true, [], []);

    /// <summary>The proposal for this technician; the moved visit, its assignment and new slot when known.</summary>
    private static RescheduleProposal Proposal(
        IncidentSchedulingInfo incident, IncidentResourceSelection selection, string technicianName, SchedulingCheckResult direct,
        VisitPlanInfo? plan, Assignment? active, SchedulingCheckResult? slot) =>
        new(
            0,
            incident.IncidentId,
            incident.RequestedStart,
            incident.RequestedEnd,
            direct.TechnicianId,
            technicianName,
            direct.VehicleId,
            direct.EquipmentIds,
            selection.TravelBufferBeforeMinutes ?? 0,
            selection.TravelBufferAfterMinutes ?? 0,
            plan?.VisitId,
            active?.Id,
            plan?.Priority.ToString(),
            slot?.Start,
            slot?.End,
            NoImpact,
            true);

    private static CandidateProposal Infeasible(RescheduleProposal proposal, string reason) =>
        new(proposal with { IsFeasible = false, Impact = new RescheduleImpact(0, 0, 0, false, [reason], []) }, null);

    /// <summary>The priority rule and the visit's state: only a Planned visit of strictly lower priority may be moved.</summary>
    private static string? MoveNotAllowed(IncidentSchedulingInfo incident, VisitPlanInfo? plan, Guid visitId) =>
        plan is null ? $"Visit '{visitId}' does not exist."
        : plan.State != VisitState.Planned ? $"Visit '{visitId}' is {plan.State}; only planned visits are moved."
        : plan.Priority >= incident.Priority
            ? $"Visit '{visitId}' has {plan.Priority} priority; an incident of {incident.Priority} priority may only move lower-priority work."
        : null;

    private static AffectedVisit Affected(VisitPlanInfo plan, DateTimeOffset newStart, DateTimeOffset newEnd) =>
        new(plan.VisitId, plan.CustomerId, plan.SiteId, plan.Start, plan.End, newStart, newEnd, (int)(newStart - plan.Start).TotalMinutes);

    private static IncidentDispatchOutcome? ValidateMove(Guid movedVisitId, ApplyRescheduleRequest request)
    {
        var errors = new ValidationErrors();
        if (movedVisitId == Guid.Empty)
        {
            errors.Add("movedVisitId", "Must not be empty.");
        }

        if (request.TechnicianId is null || request.TechnicianId == Guid.Empty)
        {
            errors.Add("technicianId", "Required.");
        }

        if (request.MovedVisitNewStartUtc is null)
        {
            errors.Add("movedVisitNewStartUtc", "Required with movedVisitId.");
        }

        if (request.MovedVisitNewEndUtc is null)
        {
            errors.Add("movedVisitNewEndUtc", "Required with movedVisitId.");
        }
        else if (request.MovedVisitNewStartUtc is { } start && request.MovedVisitNewEndUtc <= start)
        {
            errors.Add("movedVisitNewEndUtc", "Must be after the new start.");
        }

        return errors.Any ? new IncidentDispatchOutcome.Invalid(errors.ToDictionary()) : null;
    }

    private static IncidentDispatchOutcome.Invalid Invalid(string field, string message)
    {
        var errors = new ValidationErrors();
        errors.Add(field, message);
        return new IncidentDispatchOutcome.Invalid(errors.ToDictionary());
    }

    private static DateTimeOffset Latest(params DateTimeOffset[] instants) => instants.Max();
}
