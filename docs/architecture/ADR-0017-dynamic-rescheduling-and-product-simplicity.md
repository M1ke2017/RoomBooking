# ADR-0017: Dynamic rescheduling and product simplicity

- **Status:** Accepted
- **Date:** 2026-10-08
- **Sprint:** 15
- **Amends:** ADR-0012 §3 (no automatic displacement), ADR-0014 §3 (published events), ADR-0015 §6 (live message),
  ADR-0016 §4 (projected events)

## Context

An urgent incident often has exactly one good technician, and that technician is busy. Until now (ADR-0012) the system
could only say "`RequiresReschedule`" and show the conflicting visit. Moving that visit was left to the operator, by hand,
as separate operations, with no help finding a new time and no single safe step.

Sprints 12–14 added the infrastructure around the product: outbox, RabbitMQ, SignalR and Reporting. Sprint 15 returns to
the product. It closes the dispatcher's main scenario, and on the way removes layers that did not earn their place.

```
Urgent incident
  → candidates (matching, ADR-0011)
  → conflict (the scheduling check, ADR-0008)
  → reschedule proposal          (read-only)
  → manager approves
  → apply                        (ONE transaction)
  → calendar shows the new plan  (ADR-0010)
  → live change hint             (ADR-0015)
  → history and reporting        (ADR-0006, ADR-0016)
```

## Decision

### 1. Product first

- New work starts from a business scenario and its acceptance test, not from infrastructure.
- Existing modules are extended in place:
  - `UrgentIncidentService` (Scheduling) gains propose and apply;
  - `SchedulingCheckService` gains `FindNextAvailableSlotAsync`;
  - `VisitService` (WorkOrders) gains the staged move;
  - the existing `IIncidentWorkOrders` port gains two methods.
- Infrastructure stays at the edge. The product architecture is unchanged: Web → Api → WorkOrders, Scheduling,
  Workforce and Resources → PostgreSQL. Outbox, RabbitMQ, SignalR and Reporting stay around it.

### 2. Urgent incident rescheduling

- `POST /api/incidents/{id}/reschedule-proposal` returns at most **5** proposals.
- One proposal is: one technician (with the operator's vehicle, equipment and travel buffers) for the incident's
  window, and **at most one** existing visit moved out of the way.
- No cascade: if the moved visit's new slot would itself need another visit moved, there is no such slot. The search
  only accepts free slots.
- Candidates come from resource matching. Inactive, unqualified and unavailable technicians get no proposal.
- Each candidate is checked with the full scheduling check:
  - **feasible** → a direct proposal, with no move;
  - **only one visit's reservations in the way** → that visit moves;
  - **several visits, or a reservation without a visit** → an infeasible proposal that says why.
- **Impact** is the customer's view: affected visits and customers, delay in minutes, and the moved visit with its
  customer, site, and old and new time. Warnings say that the customer must be told and when the visit changes day.
- **Ordering:** feasible first; then no move; then the smallest delay; then the fewest affected visits; then the
  matching rank.

### 3. Human approval before mutation

- Nothing moves until a manager calls `POST /api/incidents/{id}/reschedule-proposal/apply` with the proposal's choices.
- The system never displaces work by itself. ADR-0012 §3 still holds for automatic behaviour. What is new is an
  explicit, approved, single step that does both changes together.
- No customer is notified automatically. The warnings only tell the manager to do it.

### 4. Proposal ≠ commit

- A proposal is advice. It writes nothing: no rows, no events, no outbox messages, no row-version bumps. A test proves
  it.
- It holds nothing either: there is no reservation hold or lock. Two managers can see the same proposal; the first apply
  wins.

### 5. Final revalidation

Apply trusts nothing from the proposal. Inside its transaction it reads the current state again:

1. **Incident.** It must still be `ReadyForDispatch` and not dispatched. Otherwise: 409 *Incident already dispatched* or
   *not ready*.
2. **Visit to move.** It must still exist, be `Planned`, keep the priority rule (§7) and still have an active
   assignment. Otherwise: 409 *Proposal out of date*, with the reason.
3. **Duration.** The new window must keep the visit's duration. Otherwise: 400.
4. **The visit's new slot,** for its own technician, vehicle and equipment, including buffers, working hours and
   absences. Not feasible: 409 *Resources not available*, with the conflicting visit as impact.
5. **The move is saved, not committed.** Then the urgent claim is checked against the plan as it will be. Not
   feasible: rollback and 409.

Any 409 leaves nothing behind. The tests compare row counts, reservations, events, outbox and the visit's row version.

### 6. Visit identity preservation

- The moved visit keeps its id, work order, notes and history. Only its `Start`/`End` change, through `Visit.TryReschedule`
  (Planned visits only).
- Its assignment is kept: same id, same resources, same buffers. Only the claim window moves (`Assignment.MoveClaim`).
- Each of its reservations (technician, vehicle, equipment) moves to the new effective window
  (`ResourceReservation.MoveTo`).
- There is no cancel-and-recreate, so the visit's history, the calendar and the reporting attribution stay continuous.

### 7. Priority rule

- An incident may only move work of **strictly lower** priority. Incident priority is compared as a work-order priority
  (ADR-0012 §9):
  - Critical and Urgent incidents can move Low, Normal and High work;
  - a High incident can move Low and Normal work.
- Equal or higher priority is never moved; the proposal is infeasible and says so.
- Only `Planned` visits move. In-progress, completed and cancelled work stays where it is.
- The rule is checked when proposing and again on apply.

### 8. Slot-search strategy

`SchedulingCheckService.FindNextAvailableSlotAsync`:

- **Same duration.** It reuses the visit's own resources and buffers.
- **Starting point.** It starts no earlier than the end of the urgent visit plus the urgent visit's travel buffer after
  plus the moved visit's travel buffer before. It never starts before the visit's original start, or before now.
- **Scan.** It scans forward in quarter hours, over a horizon of **7 days**.
- **Each step.** It runs the same scheduling check as everything else (working hours, absences, holidays, and
  reservations of the technician, vehicle and equipment, including buffers). It ignores the visit's own reservations.
  There is no second rule engine.
- **Jumps.** When reservations are in the way, it jumps past the latest one plus the buffer before. Working hours and
  absences are stepped over a quarter hour at a time.
- **Result.** The first feasible slot wins. This is not an optimizer: no route optimization and no constraint solver.

### 9. Atomic apply

One database transaction does all of it:

- the visit's move;
- its assignment's claim and reservations;
- the urgent work order, visit and assignment;
- the incident's dispatch;
- the history events: `VisitRescheduled` on the visit, and `RescheduleApplied` plus `IncidentDispatched` on the incident;
- the outbox messages: `visit.rescheduled`, `visit.created`, `incident.dispatched`, `assignment.created`.

Commit or nothing. RabbitMQ sees the messages only after the commit (ADR-0014).

Races are decided by the database, as for dispatch (ADR-0012 §7):

- the visit's, assignment's and incident's row versions;
- the reservations' exclusion constraint.

The loser is rolled back and told why:

- the incident was dispatched → 409;
- the visit was changed → 409 *Proposal out of date*;
- a resource was taken → retried, then 409 *Resources not available*.

Tested with forced races on real PostgreSQL.

### 10. A live message is an invalidation hint

- `LiveOperationMessage` is now `MessageId, Type, EntityId, OccurredAtUtc, CorrelationId?`. It means "entity X
  changed", and nothing more.
- `Related`, `EntityType`, `Action` and `Summary` are removed. The type names the entity and the change. A screen that
  needs more refreshes the read model through the REST API (ADR-0015 §8), which it had to do anyway.
- `visit.rescheduled` → a live message with `EntityId` = the visit, sent to:
  - all;
  - the incident;
  - the technician of the visit's active assignment;
  - the site.
- Group routing is unchanged in principle. It is computed next to the message, not inside it.

### 11. DTO simplification rule

- **Domain model → API contract → (optional) integration event.** One explicit mapping per arrow.
- No DTO → DTO copies in between. The proposal is built directly as its API contract (`RescheduleProposal`,
  `RescheduleImpact`, `AffectedVisit`, in `CrewCall.Contracts`). There is no internal twin.
- An interface with one implementation, no module boundary and no test seam is removed.
- Module ports stay, because they are the boundaries of ADR-0001:
  - `IIncidentWorkOrders` was extended, not duplicated;
  - `GetWorkOrderIdsAsync` was folded into `GetVisitPlansAsync`.
- Removed this sprint:
  - `IIntegrationEventMapper`: `IntegrationEventMapper` is now a static, explicit table;
  - `ICorrelationContext`: the scoped `CorrelationContext` is used directly;
  - `ILiveRoutingLookup`: replaced by the `LiveRoutingLookup` class;
  - `LiveEntityReference`, `LiveEntityTypes`, `LiveOperationActions`.

### 12. Why no new service, project or framework

- Rescheduling is a scheduling decision on data the existing modules own. It belongs in the existing
  Scheduling/WorkOrders flow, in the same transaction as dispatch.
- A new service would split that transaction. It would turn one commit into a saga, for no business gain.
- Nothing needed a new framework:
  - the existing check, matching, row versions and exclusion constraint already answer every question;
  - RabbitMQ, SignalR and Reporting only gained one event type.
- No gRPC, no new broker, no constraint solver, no AI planner, no automatic notifications.

## Consequences

- The dispatcher's main scenario works end to end, and is proven by one end-to-end test across API, Integrations
  (outbox, RabbitMQ, SignalR) and Reporting.
- The new production code is small:
  - one partial class (`UrgentIncidentService.Rescheduling.cs`);
  - one contract file (`RescheduleContracts.cs`);
  - two small migrations (the technician's phone number; the reporting reschedule counters);
  - methods on existing types.
- One visit per proposal is a deliberate limit. Days where moving one visit is not enough get an infeasible proposal.
  The manager then falls back to the manual tools (reassign, cancel).
- The slot search is linear in the horizon. It is cheap for a week at quarter-hour steps, but it is not meant for
  optimization.
- `Technician.PhoneNumber` (E.164, optional) is the contact foundation for telling a technician about an urgent change.
  Nothing sends messages yet.
- Reporting counts reschedules and their total delay per visit (`RescheduleCount`, `TotalDelayMinutes`). The visit's
  planned window follows the latest plan by business time, so a late `visit.created` never overwrites a move.
