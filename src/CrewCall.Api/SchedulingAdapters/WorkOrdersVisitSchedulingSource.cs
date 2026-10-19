using CrewCall.Scheduling.Ports;
using CrewCall.WorkOrders;
using CrewCall.WorkOrders.Visits;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.Api.SchedulingAdapters;

/// <summary>Provides Scheduling with a visit's time and status from the WorkOrders module (see ADR-0001).</summary>
internal sealed class WorkOrdersVisitSchedulingSource(IWorkOrdersDbContext workOrders) : IVisitSchedulingSource
{
    public async Task<VisitSchedulingInfo?> GetVisitAsync(Guid visitId, CancellationToken cancellationToken)
    {
        var visit = await workOrders.Visits
            .AsNoTracking()
            .Where(v => v.Id == visitId)
            .Select(v => new { v.Id, v.Start, v.End, v.Status })
            .SingleOrDefaultAsync(cancellationToken);

        return visit is null
            ? null
            : new VisitSchedulingInfo(visit.Id, visit.Start, visit.End, ToState(visit.Status));
    }

    internal static VisitState ToState(VisitStatus status) => status switch
    {
        VisitStatus.Planned => VisitState.Planned,
        VisitStatus.InProgress => VisitState.InProgress,
        VisitStatus.Completed => VisitState.Completed,
        VisitStatus.Cancelled => VisitState.Cancelled,
        _ => throw new InvalidOperationException($"Unhandled visit status {status}.")
    };
}
