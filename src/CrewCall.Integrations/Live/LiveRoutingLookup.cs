using CrewCall.Contracts.Integration;
using CrewCall.Persistence;
using CrewCall.Scheduling.Assignments;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.Integrations.Live;

/// <summary>Finds the routing context an integration event does not carry (ADR-0015).</summary>
public interface ILiveRoutingLookup
{
    Task<LiveRoutingContext> ResolveAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken);
}

/// <summary>
/// A minimal read-only lookup in the operational database, once per message (never per connection): the visit's site
/// through its work order, and the technicians of assignments the event names only by id. The v1 event contracts stay
/// unchanged. Missing rows simply mean fewer groups.
/// </summary>
public sealed class DbLiveRoutingLookup(CrewCallDbContext db) : ILiveRoutingLookup
{
    public async Task<LiveRoutingContext> ResolveAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken) =>
        integrationEvent switch
        {
            // The event names the technician; only the site is looked up.
            AssignmentCreatedIntegrationEvent created =>
                new(await SiteOfVisitAsync(created.VisitId, cancellationToken), []),
            IncidentDispatchedIntegrationEvent dispatched =>
                new(await SiteOfVisitAsync(dispatched.VisitId, cancellationToken), []),

            // Both the technician losing the visit and the one taking it over are told.
            AssignmentReplacedIntegrationEvent replaced =>
                new(await SiteOfVisitAsync(replaced.VisitId, cancellationToken),
                    await TechniciansOfAsync([replaced.OldAssignmentId, replaced.NewAssignmentId], cancellationToken)),
            AssignmentCancelledIntegrationEvent cancelled =>
                new(await SiteOfVisitAsync(cancelled.VisitId, cancellationToken),
                    await TechniciansOfAsync([cancelled.AssignmentId], cancellationToken)),

            // The technician of the visit's active assignment, if it has one.
            VisitWorkCompletedIntegrationEvent completed =>
                new(await SiteOfVisitAsync(completed.VisitId, cancellationToken),
                    await db.Assignments.AsNoTracking()
                        .Where(assignment => assignment.VisitId == completed.VisitId && assignment.Status == AssignmentStatus.Active)
                        .Select(assignment => assignment.TechnicianId)
                        .ToListAsync(cancellationToken)),

            _ => LiveRoutingContext.None
        };

    private async Task<Guid?> SiteOfVisitAsync(Guid visitId, CancellationToken cancellationToken) =>
        await (from visit in db.Visits.AsNoTracking()
               join workOrder in db.WorkOrders.AsNoTracking() on visit.WorkOrderId equals workOrder.Id
               where visit.Id == visitId
               select (Guid?)workOrder.SiteId)
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<IReadOnlyList<Guid>> TechniciansOfAsync(IReadOnlyCollection<Guid> assignmentIds, CancellationToken cancellationToken) =>
        await db.Assignments.AsNoTracking()
            .Where(assignment => assignmentIds.Contains(assignment.Id))
            .Select(assignment => assignment.TechnicianId)
            .Distinct()
            .ToListAsync(cancellationToken);
}
