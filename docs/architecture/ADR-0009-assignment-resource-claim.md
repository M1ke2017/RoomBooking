# ADR-0009: Assignment as the atomic resource claim

- **Status:** Accepted
- **Date:** 2026-10-04
- **Sprint:** 7

## Context

ADR-0008 made the scheduling check advisory: it says whether a plan *looks* feasible and writes nothing. Sprint 7 adds
the commit: assigning a technician, an optional vehicle and equipment assets to a visit, and holding those resources so
nobody else can plan them. Two dispatchers can act at the same moment, so the commit has to be correct under
concurrency, not just in sequence.

## Decision

1. **CHECK != COMMIT.**
   - `POST /api/scheduling/check` stays read-only.
   - `POST /api/visits/{visitId}/assignment` is the commit. It never trusts an earlier check. It runs the same check
     again (visit status, resources exist, skills, Workforce availability, reservations including the travel buffer)
     inside the operation that claims.
2. **One transaction per claim.**
   - `AssignmentService` runs the final check and the claim in one database transaction (under the context's
     execution strategy).
   - What it writes together: the `scheduling.assignments` row, its `scheduling.assignment_equipment` rows, one
     `scheduling.resource_reservations` row per resource, and the `AssignmentCreated` operational event. All of it
     commits or none of it does.
3. **The database is the final guard.** The check can race with another claim, and PostgreSQL decides:
   - the reservations' exclusion constraint (ADR-0008, unchanged): one booking per resource at a time;
   - the partial unique index `ux_assignments_one_active_per_visit` on `visit_id WHERE status = 'Active'`: one active
     assignment per visit;
   - a row version (xmin) on assignments: concurrent reassign or cancel of the same assignment.

   A request that loses a race rolls back completely and gets **409** with the reasons (re-evaluated after the
   rollback, so they name the winner's reservation), never 500. Nothing is locked in memory.

   Two transactions inserting conflicting rows under an exclusion constraint can wait for each other. PostgreSQL then
   aborts one of them with a deadlock (`40P01`) rather than an exclusion violation (`23P01`). Both count as a lost race.
   If the conflict is not visible yet, because the winner has not committed, the whole check-and-claim is attempted
   again, at most 3 times. The retried insert waits for the winner's commit and then fails with a plain exclusion
   violation, which is explained and returned as 409.
4. **Reservations use the buffered window.**
   - An assignment reserves `[visit start - before buffer, visit end + after buffer)`, the same window the check
     tested. The buffer is not a separate reservation.
   - The assignment keeps both buffer values and the claimed window (`claimed_start` / `claimed_end`).
   - Each reservation points to its assignment (`assignment_id`, a foreign key within the scheduling schema).
5. **Assignments have history.** Status is `Active | Replaced | Cancelled`. Rows are never deleted.
   - **Reassignment** creates a new assignment. In one transaction, in this order:
     1. release the old assignment's reservations;
     2. mark the old assignment `Replaced` (with `replaced_by_assignment_id`);
     3. insert the new assignment and its reservations;
     4. append `AssignmentReplaced` (on the old assignment) and `AssignmentCreated` (on the new one).

     The new check ignores this visit's own reservations (self-exclusion by visit id), but not those of other visits.
     If anything fails, the old assignment and its reservations stay exactly as they were.
   - **Cancellation** releases the reservations, marks the assignment `Cancelled` and appends `AssignmentCancelled`,
     in one transaction. It is idempotent: with no active assignment it changes nothing and returns 204.
6. **Visit stores no resources.** Visit (WorkOrders) has no technician, vehicle or equipment fields. Assignment is a
   separate Scheduling model.
   - It refers to the visit and to the resources by id, without cross-module foreign keys (ADR-0001).
   - It reads visits, technicians and resources through ports implemented in the composition root.
7. **Events stay local** in `ops.operational_events` (ADR-0006, append-only). There is no outbox, message broker or
   push notifications yet.

## Consequences

- Double booking is impossible, and a visit cannot get two active assignments, whatever the timing of requests.
- Every claim either fully exists (assignment, equipment, reservations, event) or does not exist at all. There are no
  orphan reservations or events.
- The complete assignment history of a visit can be read back (`GET /api/visits/{visitId}/assignments/history`), and
  the operational events record every change.
- Changing a visit's time or status does not yet move or release its assignment. That belongs to a later
  rescheduling sprint.
