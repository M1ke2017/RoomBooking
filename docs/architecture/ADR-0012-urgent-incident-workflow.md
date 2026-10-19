# ADR-0012: Urgent incident workflow

- **Status:** Accepted
- **Date:** 2026-10-06
- **Sprint:** 10

## Context

An urgent incident (a power outage, a gas leak) needs a technician now. The system already knows who *could* go:
matching ranks candidates (ADR-0011), the scheduling check finds conflicts (ADR-0008), and an assignment claims resources
atomically (ADR-0009). What it lacked was one flow from "something is wrong at this site" to "a technician is on the way",
recorded as regular work.

The temptation in an urgent flow is to let the system act: pick the best candidate, push other visits aside, dispatch.
That is exactly what dispatchers must stay in control of. Moving someone else's visit has consequences the system
cannot see: a customer promise, a part already on a van, a technician's skills needed elsewhere.

## Decision

The workflow is **Incident → Analyze → Suggest → Prepare → Dispatch**:

- **Incident** belongs to WorkOrders (ADR-0001). It has a priority (`High`, `Urgent`, `Critical`), a requested window and
  required skill codes. Its controlled lifecycle:
  - `New → Analyzing | Cancelled`;
  - `Analyzing → ReadyForDispatch | Cancelled`;
  - `ReadyForDispatch → Dispatched | Cancelled`;
  - `Dispatched → Resolved`.
- **Analyze, Prepare and Dispatch** live in Scheduling (`UrgentIncidentService`). They reach WorkOrders through the
  `IIncidentWorkOrders` port, implemented in the composition root.

### 1. Recommendation != action

Analyze produces recommendations; only Dispatch acts. A suggestion is never applied by itself, not even the top one,
and not even for a Critical incident.

### 2. Operator decision required

Dispatch takes the operator's explicit selection (technician, optional vehicle and equipment, travel buffers). There is
no "dispatch the best candidate" operation.

### 3. No automatic displacement

The system never moves, cancels or reassigns existing visits or assignments, and never pushes lower-priority work aside.

- A candidate who is feasible (active, skilled, available) but whose existing bookings collide is suggested as
  `RequiresReschedule`, together with the **impact**: the conflicting reservation, its visit, that visit's work order,
  the resource and the reserved window.
- Rescheduling is the operator's decision and an explicit separate action (reassign, cancel) on that other work.
- *Sprint 15 (ADR-0017):* a reschedule **proposal** may suggest moving ONE strictly lower-priority planned visit to its
  next free slot. It moves only when a manager applies it, in one transaction with the dispatch. Nothing is ever
  displaced automatically.
- Likewise, cancelling a dispatched incident is refused (409). Its work order, visit and assignment are never cancelled
  as a hidden side effect.

### 4. Analyze is advisory

- Analyze writes only the incident's own state: `New → Analyzing → ReadyForDispatch` plus an `IncidentAnalyzed` event
  with counts. It claims nothing and stores no suggestion snapshot.
- Analysis can be repeated while the incident is not dispatched, because the plan may change.
- **Classification:** the pure F# `DispatchSuggestions.classify` decides on the unchanged Sprint 9 matching inputs:
  - `DirectAssignment`: matching eligible, with the same score and rank order;
  - `RequiresReschedule`: feasible, but booked. The score is what the candidate would score once free; the workload
    still counts the conflicting work;
  - `Unavailable`: inactive, missing skills or unavailable. It has no score.
- **Limits:**
  - matching evaluates up to 200 candidates (or the requested subset);
  - the top 10 direct candidates (`MaxCheckedCandidates`) get a full scheduling check, and a candidate that fails it
    is reclassified by its reasons;
  - up to 10 `RequiresReschedule` and 10 `Unavailable` suggestions are returned (`MaxSuggestionsPerType`);
  - the totals (`EligibleCount`, `RejectedCount`, `CandidatesEvaluated`) are always reported.

### 5. Prepare Dispatch is advisory

Prepare runs exactly the final check Dispatch will run, for the operator's selection, and returns `CanDispatch`, the
reasons and the impact. It always returns HTTP 200, also when `CanDispatch` is false, and it writes nothing.

### 6. Dispatch is the commit

Dispatch re-validates everything inside its transaction:

- the incident is `ReadyForDispatch`;
- it has no work order yet;
- the final scheduling check passes.

Analysis and preparation are only advice: the plan may have changed since.

### 7. Resource constraints remain the final guard

- The resource claim is the Sprint 7 claim itself, not a copy. `AssignmentService` exposes its building blocks
  internally: `FinalCheckAsync`, `StageClaim`, `ExplainRejectionAsync`, `WithClaimRetriesAsync`, `IsRejectedWrite`.
  Assignment and dispatch use the same ones.
- The database decides every race:
  - **Resources:** the reservations' exclusion constraint. Two incidents racing for one technician, vehicle or asset
    give one 201 and one 409; the loser stays `ReadyForDispatch`.
  - **Duplicate dispatch:** the incident's row version (xmin). Two dispatches of one incident give one 201 and one 409,
    and exactly one work order, even when they choose different technicians.
  - Backstops: `work_order_id` is unique, and a CHECK constraint ties `work_order_id` to the `Dispatched`/`Resolved`
    states.

### 8. Atomic Incident → WorkOrder → Visit → Assignment

One transaction (under the execution strategy, with the claim retries) contains everything:

- the work order, created through WorkOrders' regular creation path, with `WorkOrderCreated`;
- its `Planned` visit over the requested window, with `VisitCreated`;
- the assignment, its equipment and resource reservations, with `AssignmentCreated`;
- the incident's link to the work order, its move to `Dispatched`, and `IncidentDispatched` (with the work order,
  visit, assignment, technician, vehicle and equipment ids).

WorkOrders *stages* its part into the request's single DbContext without saving. Scheduling stages the claim, then saves
and commits once. Any failure rolls back all of it: a lost race (409), a concurrent change (409), or a database error
(500). This relies on the composition root resolving every module's data interface to one scoped `CrewCallDbContext`
(ADR-0005).

### 9. A Critical incident maps to an Urgent work order

`WorkOrderPriority` stays `Low | Normal | High | Urgent`; there is no `Critical` work order:

| Incident priority | Work order priority |
|---|---|
| High | High |
| Urgent | Urgent |
| Critical | Urgent |

The incident keeps its own `Critical` priority.

## Consequences

- Dispatchers get a complete, explainable picture of who can go now and what each alternative would cost. The system
  never makes the trade-off for them.
- One commit path for resources: whatever protects an assignment also protects a dispatch, and any future rule added to
  the claim applies to both.
- The atomic dispatch depends on the shared scoped DbContext. If WorkOrders or Scheduling ever becomes a separate
  service (ADR-0003), this becomes a distributed workflow (outbox, saga), and this ADR must be revisited.
- HTTP:
  - `POST /api/incidents/{id}/dispatch` returns **201 Created**, with `Location` pointing to the new work order; the
    body holds the incident, work order, visit and assignment;
  - 400 for validation, 404 for a missing incident or resource, 409 for lifecycle conflicts, duplicates and taken
    resources;
  - resolve and cancel return 200 with the incident.
- Out of scope: automatic displacement or rescheduling, notifications (SignalR), messaging (RabbitMQ, outbox), field
  statuses, and any UI.
