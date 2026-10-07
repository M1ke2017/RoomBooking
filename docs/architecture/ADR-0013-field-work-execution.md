# ADR-0013: Field work execution

- **Status:** Accepted
- **Date:** 2026-10-07
- **Sprint:** 11

## Context

A visit has a planned window (`Visit.Start`/`End`) and a coarse status (`Planned`, `InProgress`, `Completed`,
`Cancelled`). What happens in the field is different and finer-grained: the technician sets off at 08:35, starts work
at 09:07, pauses from 09:30 to 09:42 and finishes at 10:18. Reporting (later) needs those facts, and dispatchers need to
see where the work stands right now. Planned and actual times must never be mixed up.

## Decision

1. **Visit = plan, VisitExecution = actual execution.**
   - `VisitExecution` is a separate record, owned by WorkOrders, with at most one per visit (unique `visit_id`).
   - It holds its own status, `FieldWorkStatus`: NotStarted, Traveling, Working, Paused, Completed, Cancelled. This
     is neither a visit status nor a work order status.
   - It also holds the actual timestamps: `TravelStartedAtUtc`, `WorkStartedAtUtc`, `CompletedAtUtc`,
     `CancelledAtUtc`.
   - The visit's planned times are never changed by execution. A visit without an execution record is NotStarted.
2. **Controlled lifecycle:**
   - NotStarted → Traveling | Working | Cancelled;
   - Traveling → Working | Cancelled;
   - Working → Paused | Completed | Cancelled;
   - Paused → Working | Completed | Cancelled.
   - Completed and Cancelled are terminal.
   - Work can start without travel (the technician is already on site). Starting work while paused is a resume, and
     the first `WorkStartedAtUtc` is kept.
   - Starting travel or work requires an open visit with an **active assignment**, so the execution represents really
     assigned work. WorkOrders asks through the `IActiveAssignmentCheck` port, implemented in the composition root
     (ADR-0001).
3. **Timestamps, not mutable duration counters.**
   - Nothing like "minutes worked so far" is stored or incremented. Facts are recorded once, at the instant they happen.
4. **Pauses are separate records.**
   - `VisitExecutionPause` has `StartedAtUtc` and `EndedAtUtc?`, so the full history of breaks is kept.
   - Resume, complete and cancel all close the open pause at their own instant.
   - At most one open pause per execution, guarded twice: by the lifecycle (a pause needs Working) and by a partial
     unique index `WHERE ended_at_utc IS NULL`.
5. **Metrics are derived.** `VisitExecutionMetrics.Calculate` is pure:
   - travel = work start − travel start;
   - gross work = completion − work start;
   - pause = sum of closed pauses;
   - net work = gross − pause.

   Gross and net exist only for a Completed execution (`IsFinal = true`). Before that, travel and closed pauses are
   shown and the final work duration is not invented. The same helper serves the execution endpoints, the completion
   event and the operational calendar.
6. **TimeProvider.** Every timestamp comes from the injected `TimeProvider`, read once per operation, so the status,
   the timestamps, a closed pause and the events agree. Tests use a manual clock for exact durations, and a barrier
   clock to force concurrent requests through the same state.
7. **Execution events are the basis for future reporting.**
   - Events: `VisitTravelStarted`, `VisitWorkStarted`, `VisitWorkPaused`, `VisitWorkResumed`, `VisitWorkCompleted`
     (with travel, gross, pause and net minutes), `VisitExecutionCancelled`.
   - They are appended to the existing operational event log on the visit's aggregate, in the same SaveChanges as the
     state change. The log's sequence orders the plan's and the execution's history together. There is no separate
     event store.
8. **Concurrency.**
   - The execution carries an xmin row version. Two concurrent first changes collide on the unique visit, and two
     pauses collide on the open-pause index or the row version.
   - The loser gets 409 with nothing saved: no second pause, no event, and never two terminal states.
9. **No full automatic synchronization of Visit or Assignment in this sprint.**
   - Only two unambiguous visit steps follow, through the visit's own lifecycle (`VisitService.TryChangeStatus`, the
     same path as the visit status endpoint): the first start of work moves a Planned visit to InProgress, and
     completion moves an InProgress visit to Completed.
   - Nothing else is synchronized:
     - cancelling an execution leaves the visit, the assignment and its reservations as they are;
     - completing the work does not complete the work order and releases no reservation;
     - reassigning or cancelling a visit that is being executed is not yet coordinated with its execution.

## Consequences

- Planned and actual stay separable, which is the basis for plan-versus-actual reporting.
- Durations can always be recomputed and corrected from the facts, and a metric definition can change without
  migrating stored numbers.
- The operational calendar shows each visit's field work status, actual timestamps and derived minutes. That adds one
  conditional query for pauses, run only when the range contains recorded executions.
- Open points for later sprints:
  - releasing resources after completion or cancellation;
  - reassignment during execution;
  - work order completion;
  - field worker UI and notifications (SignalR);
  - a reporting service consuming the execution events.
