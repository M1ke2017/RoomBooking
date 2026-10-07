using System.Data.Common;
using CrewCall.WorkOrders.Operations;
using CrewCall.WorkOrders.Visits;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.WorkOrders.Executions;

/// <summary>
/// Records the actual field work of a visit (ADR-0013): travel, work, pauses, completion or cancellation, each with its
/// operational event in the same SaveChanges. Times come from <see cref="TimeProvider"/>, never from the system clock
/// directly. The execution's row version (and the unique visit and open-pause indexes) make concurrent changes fail
/// instead of producing contradictory states: the loser gets ConcurrentChange (409) and nothing of it is saved.
/// </summary>
/// <remarks>
/// Assignments and reservations are not touched: completing or cancelling field work releases nothing. The visit's
/// status follows only the two unambiguous steps, through the visit's own lifecycle: the first start of work moves a
/// Planned visit to InProgress, completion moves an InProgress visit to Completed.
/// </remarks>
public sealed class VisitExecutionService(IWorkOrdersDbContext db, IActiveAssignmentCheck assignments, TimeProvider clock)
{
    /// <summary>NotStarted → Traveling. Needs an open visit with an active assignment.</summary>
    public Task<VisitExecutionOutcome> StartTravelAsync(Guid visitId, CancellationToken cancellationToken) =>
        ChangeAsync(visitId, FieldWorkAction.StartTravel, requireAssignedOpenVisit: true, cancellationToken, (context, now) =>
        {
            if (!context.Execution.TryStartTravel(now))
            {
                return false;
            }

            Append(VisitExecutionEvents.VisitTravelStarted, context, now, new VisitTravelStartedPayload(context.Visit.Id, context.Execution.Id, now));
            return true;
        });

    /// <summary>
    /// Starts work: from NotStarted (no travel recorded) or Traveling it is the first start (WorkStartedAtUtc, and a
    /// Planned visit becomes InProgress); from Paused it is a resume, and the first start time is kept. Needs an open
    /// visit with an active assignment.
    /// </summary>
    public Task<VisitExecutionOutcome> StartWorkAsync(Guid visitId, CancellationToken cancellationToken) =>
        ChangeAsync(visitId, FieldWorkAction.StartWork, requireAssignedOpenVisit: true, cancellationToken, (context, now) =>
            context.Execution.Status == FieldWorkStatus.Paused ? Resume(context, now) : StartWork(context, now));

    /// <summary>Working → Paused, opening a new pause.</summary>
    public Task<VisitExecutionOutcome> PauseAsync(Guid visitId, CancellationToken cancellationToken) =>
        ChangeAsync(visitId, FieldWorkAction.Pause, requireAssignedOpenVisit: false, cancellationToken, (context, now) =>
        {
            if (context.Execution.TryPause(Guid.CreateVersion7(), now) is not { } pause)
            {
                return false;
            }

            db.VisitExecutionPauses.Add(pause);
            Append(VisitExecutionEvents.VisitWorkPaused, context, now, new VisitWorkPausedPayload(context.Visit.Id, context.Execution.Id, pause.Id, now));
            return true;
        });

    /// <summary>Paused → Working, closing the open pause.</summary>
    public Task<VisitExecutionOutcome> ResumeAsync(Guid visitId, CancellationToken cancellationToken) =>
        ChangeAsync(visitId, FieldWorkAction.Resume, requireAssignedOpenVisit: false, cancellationToken, Resume);

    /// <summary>
    /// Working or Paused → Completed (an open pause ends at the same instant), with the final metrics in the event. An
    /// InProgress visit becomes Completed; the work order is not completed.
    /// </summary>
    public Task<VisitExecutionOutcome> CompleteAsync(Guid visitId, CancellationToken cancellationToken) =>
        ChangeAsync(visitId, FieldWorkAction.Complete, requireAssignedOpenVisit: false, cancellationToken, (context, now) =>
        {
            var openPause = context.Execution.OpenPause;
            if (!context.Execution.TryComplete(now))
            {
                return false;
            }

            var metrics = VisitExecutionMetrics.For(context.Execution);
            Append(VisitExecutionEvents.VisitWorkCompleted, context, now, new VisitWorkCompletedPayload(
                context.Visit.Id,
                context.Execution.Id,
                now,
                VisitExecutionMetrics.Minutes(metrics.Travel),
                VisitExecutionMetrics.Minutes(metrics.GrossWork!.Value),
                VisitExecutionMetrics.Minutes(metrics.Pause),
                VisitExecutionMetrics.Minutes(metrics.NetWork!.Value),
                openPause?.Id));

            if (context.Visit.Status == VisitStatus.InProgress)
            {
                VisitService.TryChangeStatus(db, context.Visit, VisitStatus.Completed, now);
            }

            return true;
        });

    /// <summary>
    /// Any non-terminal status → Cancelled (an open pause ends at the same instant). The assignment and its reservations
    /// stay: releasing them is a separate decision.
    /// </summary>
    public Task<VisitExecutionOutcome> CancelAsync(Guid visitId, CancellationToken cancellationToken) =>
        ChangeAsync(visitId, FieldWorkAction.Cancel, requireAssignedOpenVisit: false, cancellationToken, (context, now) =>
        {
            var previous = context.Execution.Status;
            var openPause = context.Execution.OpenPause;
            if (!context.Execution.TryCancel(now))
            {
                return false;
            }

            Append(VisitExecutionEvents.VisitExecutionCancelled, context, now, new VisitExecutionCancelledPayload(
                context.Visit.Id, context.Execution.Id, now, previous, openPause?.Id));
            return true;
        });

    /// <summary>The visit's execution, or null when nothing was recorded yet (NotStarted); VisitFound is false for a missing visit.</summary>
    public async Task<(bool VisitFound, VisitExecution? Execution)> GetAsync(Guid visitId, CancellationToken cancellationToken)
    {
        if (!await db.Visits.AnyAsync(visit => visit.Id == visitId, cancellationToken))
        {
            return (false, null);
        }

        var execution = await db.VisitExecutions
            .AsNoTracking()
            .Include(e => e.Pauses.OrderBy(pause => pause.StartedAtUtc))
            .SingleOrDefaultAsync(e => e.VisitId == visitId, cancellationToken);

        return (true, execution);
    }

    private sealed record Context(Visit Visit, VisitExecution Execution);

    private bool StartWork(Context context, DateTimeOffset now)
    {
        if (!context.Execution.TryStartWork(now))
        {
            return false;
        }

        var travel = VisitExecutionMetrics.For(context.Execution).Travel;
        Append(VisitExecutionEvents.VisitWorkStarted, context, now, new VisitWorkStartedPayload(
            context.Visit.Id, context.Execution.Id, now, VisitExecutionMetrics.Minutes(travel)));

        if (context.Visit.Status == VisitStatus.Planned)
        {
            VisitService.TryChangeStatus(db, context.Visit, VisitStatus.InProgress, now);
        }

        return true;
    }

    private bool Resume(Context context, DateTimeOffset now)
    {
        if (context.Execution.TryResume(now) is not { } pause)
        {
            return false;
        }

        Append(VisitExecutionEvents.VisitWorkResumed, context, now, new VisitWorkResumedPayload(
            context.Visit.Id, context.Execution.Id, pause.Id, now, VisitExecutionMetrics.Minutes(pause.EndedAtUtc!.Value - pause.StartedAtUtc)));
        return true;
    }

    private void Append(string eventType, Context context, DateTimeOffset now, object payload) =>
        db.AppendOperationalEvent(eventType, WorkOrderEvents.VisitAggregate, context.Visit.Id, now, payload);

    /// <summary>
    /// Loads the visit and its execution (a new, NotStarted one when none exists yet), applies <paramref name="change"/>
    /// at one instant from the clock, and saves the change and its events together.
    /// </summary>
    private async Task<VisitExecutionOutcome> ChangeAsync(
        Guid visitId,
        FieldWorkAction action,
        bool requireAssignedOpenVisit,
        CancellationToken cancellationToken,
        Func<Context, DateTimeOffset, bool> change)
    {
        var visit = await db.Visits.SingleOrDefaultAsync(v => v.Id == visitId, cancellationToken);
        if (visit is null)
        {
            return new VisitExecutionOutcome.VisitNotFound(visitId);
        }

        if (requireAssignedOpenVisit)
        {
            if (visit.Status is VisitStatus.Completed or VisitStatus.Cancelled)
            {
                return new VisitExecutionOutcome.VisitClosed(visitId, visit.Status);
            }

            if (!await assignments.HasActiveAssignmentAsync(visitId, cancellationToken))
            {
                return new VisitExecutionOutcome.NoActiveAssignment(visitId);
            }
        }

        var execution = await db.VisitExecutions
            .Include(e => e.Pauses)
            .SingleOrDefaultAsync(e => e.VisitId == visitId, cancellationToken);

        // One instant for the whole change: the new status, its timestamps, a closed pause and the events agree.
        var now = StoredTime.UtcNow(clock);

        var isNew = execution is null;
        execution ??= new VisitExecution(Guid.CreateVersion7(), visitId, now);
        var from = execution.Status;
        if (!change(new Context(visit, execution), now))
        {
            return new VisitExecutionOutcome.TransitionNotAllowed(from, action);
        }

        if (isNew)
        {
            db.VisitExecutions.Add(execution);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The execution (or the visit) changed since it was loaded: a concurrent change won.
            return new VisitExecutionOutcome.ConcurrentChange(visitId);
        }
        catch (DbUpdateException exception) when (exception.InnerException is DbException { SqlState: "23505" })
        {
            // A concurrent first change created the execution, or opened a pause, first (unique visit / open pause).
            return new VisitExecutionOutcome.ConcurrentChange(visitId);
        }

        return new VisitExecutionOutcome.Changed(execution);
    }
}
