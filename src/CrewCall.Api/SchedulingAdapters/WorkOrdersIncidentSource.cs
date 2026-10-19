using CrewCall.Scheduling.Ports;
using CrewCall.WorkOrders;
using CrewCall.WorkOrders.Incidents;
using CrewCall.WorkOrders.Visits;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.Api.SchedulingAdapters;

/// <summary>
/// Gives Scheduling's incident workflow the WorkOrders operations it needs (ADR-0001, ADR-0012). The lifecycle rules,
/// the work order and visit creation and the incident events stay in WorkOrders' <see cref="IncidentService"/>; this
/// adapter only translates. Staging writes into the request's one DbContext, which Scheduling then saves and commits.
/// </summary>
internal sealed class WorkOrdersIncidentSource(IWorkOrdersDbContext workOrders, IncidentService incidents, VisitService visits) : IIncidentWorkOrders
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
                i.Priority,
                Skills = i.RequiredSkills.Select(skill => skill.SkillCode).ToList()
            })
            .SingleOrDefaultAsync(cancellationToken);

        return incident is null
            ? null
            : new IncidentSchedulingInfo(
                incident.Id, ToState(incident.Status), incident.RequestedStart, incident.RequestedEnd, incident.Skills.Order().ToList(), incident.WorkOrderId,
                ToPriority(IncidentLifecycle.ToWorkOrderPriority(incident.Priority)));
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

    public async Task<IReadOnlyDictionary<Guid, VisitPlanInfo>> GetVisitPlansAsync(IReadOnlyCollection<Guid> visitIds, CancellationToken cancellationToken)
    {
        var ids = visitIds.ToList();
        var plans = await (
                from visit in workOrders.Visits.AsNoTracking()
                join workOrder in workOrders.WorkOrders.AsNoTracking() on visit.WorkOrderId equals workOrder.Id
                where ids.Contains(visit.Id)
                select new { visit.Id, visit.WorkOrderId, workOrder.CustomerId, workOrder.SiteId, workOrder.Priority, visit.Start, visit.End, visit.Status })
            .ToListAsync(cancellationToken);

        return plans.ToDictionary(
            plan => plan.Id,
            plan => new VisitPlanInfo(
                plan.Id, plan.WorkOrderId, plan.CustomerId, plan.SiteId, ToPriority(plan.Priority), plan.Start, plan.End,
                WorkOrdersVisitSchedulingSource.ToState(plan.Status)));
    }

    public Task<bool> StageVisitRescheduleAsync(VisitReschedulePlan plan, CancellationToken cancellationToken) =>
        visits.StageRescheduleForIncidentAsync(
            new StageVisitReschedule(plan.VisitId, plan.NewStart, plan.NewEnd, plan.IncidentId, plan.UrgentVisitId, plan.TechnicianId),
            cancellationToken);

    private static PlanPriority ToPriority(WorkOrderPriority priority) => priority switch
    {
        WorkOrderPriority.Low => PlanPriority.Low,
        WorkOrderPriority.Normal => PlanPriority.Normal,
        WorkOrderPriority.High => PlanPriority.High,
        WorkOrderPriority.Urgent => PlanPriority.Urgent,
        _ => throw new InvalidOperationException($"Unhandled work order priority {priority}.")
    };

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
