using CrewCall.Scheduling.Ports;
using CrewCall.WorkOrders;
using CrewCall.WorkOrders.Incidents;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.Api.SchedulingAdapters;

/// <summary>
/// Gives Scheduling's incident workflow the WorkOrders operations it needs (ADR-0001, ADR-0012). The lifecycle rules,
/// the work order and visit creation and the incident events stay in WorkOrders' <see cref="IncidentService"/>; this
/// adapter only translates. Staging writes into the request's one DbContext, which Scheduling then saves and commits.
/// </summary>
internal sealed class WorkOrdersIncidentSource(IWorkOrdersDbContext workOrders, IncidentService incidents) : IIncidentWorkOrders
{
    public async Task<IncidentSchedulingInfo?> GetIncidentAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        var incident = await workOrders.Incidents
            .AsNoTracking()
            .Where(i => i.Id == incidentId)
            .Select(i => new
            {
                i.Id,
                i.Status,
                i.RequestedStart,
                i.RequestedEnd,
                i.WorkOrderId,
                Skills = i.RequiredSkills.Select(skill => skill.SkillCode).ToList()
            })
            .SingleOrDefaultAsync(cancellationToken);

        return incident is null
            ? null
            : new IncidentSchedulingInfo(
                incident.Id, ToState(incident.Status), incident.RequestedStart, incident.RequestedEnd, incident.Skills.Order().ToList(), incident.WorkOrderId);
    }

    public async Task<IncidentStateChange> BeginAnalysisAsync(Guid incidentId, CancellationToken cancellationToken) =>
        ToChange(await incidents.BeginAnalysisAsync(incidentId, cancellationToken));

    public async Task<IncidentStateChange> CompleteAnalysisAsync(IncidentAnalysisSummary analysis, CancellationToken cancellationToken) =>
        ToChange(await incidents.CompleteAnalysisAsync(
            new RecordIncidentAnalysis(
                analysis.IncidentId,
                analysis.GeneratedAtUtc,
                analysis.CandidatesEvaluated,
                analysis.EligibleCount,
                analysis.RejectedCount,
                analysis.DirectAssignmentCount,
                analysis.RequiresRescheduleCount,
                analysis.UnavailableCount),
            cancellationToken));

    public async Task<IncidentDispatchStaging> StageDispatchAsync(IncidentDispatchPlan plan, CancellationToken cancellationToken)
    {
        var outcome = await incidents.StageDispatchAsync(
            new StageIncidentDispatch(plan.IncidentId, plan.VisitId, plan.AssignmentId, plan.TechnicianId, plan.VehicleId, plan.EquipmentIds),
            cancellationToken);

        return outcome switch
        {
            StageIncidentDispatchOutcome.Staged staged => new IncidentDispatchStaging.Staged(staged.WorkOrder.Id, staged.Visit.Id),
            StageIncidentDispatchOutcome.AlreadyDispatched dispatched => new IncidentDispatchStaging.AlreadyDispatched(dispatched.WorkOrderId),
            StageIncidentDispatchOutcome.NotReady notReady => new IncidentDispatchStaging.NotReady(ToState(notReady.Status)),
            StageIncidentDispatchOutcome.NotFound notFound => new IncidentDispatchStaging.NotFound(notFound.IncidentId),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    public async Task<IReadOnlyDictionary<Guid, Guid>> GetWorkOrderIdsAsync(IReadOnlyCollection<Guid> visitIds, CancellationToken cancellationToken)
    {
        var ids = visitIds.ToList();
        return await workOrders.Visits
            .AsNoTracking()
            .Where(visit => ids.Contains(visit.Id))
            .ToDictionaryAsync(visit => visit.Id, visit => visit.WorkOrderId, cancellationToken);
    }

    private static IncidentStateChange ToChange(ChangeIncidentOutcome outcome) => outcome switch
    {
        ChangeIncidentOutcome.Changed changed => new IncidentStateChange.Changed(ToState(changed.Incident.Status)),
        ChangeIncidentOutcome.NotFound notFound => new IncidentStateChange.NotFound(notFound.IncidentId),
        ChangeIncidentOutcome.TransitionNotAllowed notAllowed => new IncidentStateChange.NotAllowed(ToState(notAllowed.From)),
        ChangeIncidentOutcome.ConcurrentChange concurrent => new IncidentStateChange.ConcurrentChange(concurrent.IncidentId),
        _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
    };

    private static IncidentState ToState(IncidentStatus status) => status switch
    {
        IncidentStatus.New => IncidentState.New,
        IncidentStatus.Analyzing => IncidentState.Analyzing,
        IncidentStatus.ReadyForDispatch => IncidentState.ReadyForDispatch,
        IncidentStatus.Dispatched => IncidentState.Dispatched,
        IncidentStatus.Resolved => IncidentState.Resolved,
        IncidentStatus.Cancelled => IncidentState.Cancelled,
        _ => throw new InvalidOperationException($"Unhandled incident status {status}.")
    };
}
