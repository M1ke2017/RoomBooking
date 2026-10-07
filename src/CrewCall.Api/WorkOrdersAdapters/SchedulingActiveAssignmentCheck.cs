using CrewCall.Scheduling;
using CrewCall.Scheduling.Assignments;
using CrewCall.WorkOrders.Executions;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.Api.WorkOrdersAdapters;

/// <summary>Answers WorkOrders' field work question "is this visit assigned?" from Scheduling's assignments (ADR-0001).</summary>
internal sealed class SchedulingActiveAssignmentCheck(ISchedulingDbContext scheduling) : IActiveAssignmentCheck
{
    public Task<bool> HasActiveAssignmentAsync(Guid visitId, CancellationToken cancellationToken) =>
        scheduling.Assignments.AnyAsync(
            assignment => assignment.VisitId == visitId && assignment.Status == AssignmentStatus.Active, cancellationToken);
}
