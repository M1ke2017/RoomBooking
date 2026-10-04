# ADR-0006: Operational events: relational state plus append-only history

- **Status:** Accepted
- **Date:** 2026-10-04
- **Sprint:** 4

## Context

CrewCall's reporting (worked time, reschedules, interruptions, utilization) must come from what actually happened,
not from the latest state of a calendar. Sprint 4 introduces the first state changes worth recording: work orders and
visits being created and moving through their lifecycles.

Full event sourcing would make every read a projection and every change a stream append. That is more machinery than
CrewCall needs now.

## Decision

1. **Operational state stays relational** (`workorders.work_orders`, `workorders.visits`, ...). It remains the source of
   truth for the current state.
2. **Every significant state change also appends an operational event** to `ops.operational_events`:
   `id`, `sequence` (database-assigned, strictly increasing), `occurred_at_utc`, `event_type`, `aggregate_type`,
   `aggregate_id`, `payload` (jsonb), `correlation_id` (reserved, not populated yet).
3. **Same transaction.** A module appends an event through its DbContext interface
   (`IWorkOrdersDbContext.AppendOperationalEvent`). That only adds the row to the current unit of work. The module's
   single `SaveChangesAsync` then writes the state change and the event in one database transaction. There is never a
   separate save for events. If either write fails, neither is committed.
4. **Append-only is enforced by the database.** A trigger rejects `UPDATE`, `DELETE` and `TRUNCATE` on
   `ops.operational_events`. No code path updates or deletes events.
5. **Payloads are small, purpose-made records** (for example `WorkOrderStatusChangedPayload(WorkOrderId, OldStatus,
   NewStatus)`), defined by the module that owns the event. Entities are never serialized. Enums are written as names.
   Event contracts are not versioned yet.
6. **Event names and aggregate types are owned by the module** (`WorkOrderEvents`). Persistence only stores them.
7. Reading the history is a technical, diagnostic endpoint
   (`GET /api/operations/events/{aggregateType}/{aggregateId}`), ordered by `occurred_at_utc`, then `sequence`.
   Reporting projections are a later concern.

## Consequences

- History and state can never disagree: an event exists if and only if its state change was committed.
- Later sprints can build reporting projections, outbox publishing (RabbitMQ) and live updates (SignalR) from the same
  table, without changing how modules record events.
- The events table grows without bound; partitioning or archiving can be added later without changing writers.
- Because events are written in the module's transaction, a module that will be extracted into its own service (ADR-0003)
  takes its events with it and writes them to its own store.
