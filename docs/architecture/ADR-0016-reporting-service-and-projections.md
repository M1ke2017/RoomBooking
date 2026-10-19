# ADR-0016: Reporting service and event-driven projections

- **Status:** Accepted
- **Date:** 2026-10-08
- **Sprint:** 14

## Context

Dispatchers and managers need reports: what a technician did over a period, how planned visits compared with the actual
work, how many incidents were dispatched. The operational system answers "what is the state now"; reports aggregate
history over days and months. Sprint 12 (ADR-0014) publishes committed changes as integration events on RabbitMQ.
Sprint 14 builds the first read side that is a real service boundary on top of them: **CrewCall.Reporting**.

This is not event sourcing. The operational database stays the system of record; Reporting keeps **event-driven read
projections** of it.

## Decision

```
                 OPERATIONAL SIDE

API
 |
 v
Business State
 |
 v
Outbox
 |
 v
RabbitMQ                      (crewcall.events)
 |
 +-----------------------------+
 |                             |
 v                             v
Live Operations            Reporting
Consumer                    Consumer          (crewcall.reporting)
 |                             |
 v                             v
SignalR               Reporting Inbox        (reporting.inbox_messages)
                               |
                               v
                     Projection Processor
                               |
                  +------------+------------+
                  |            |            |
                  v            v            v
              Visits      Technicians   Incidents      (+ assignments)
                  \            |           /
                   \           |          /
                    +----------+---------+
                               |
                               v
                        Reporting API          (/api/reporting/...)
```

### 1. Reporting is a separate service

- **Own process:** `CrewCall.Reporting`, an ASP.NET Core service under Aspire, with its own lifecycle and health. It is
  not part of CrewCall.Api.
- **Owns three things:** its RabbitMQ consumer, its projections and the read-only Reporting API.
- **Why separate:** reporting has a different load profile (long ranges, aggregation), a different consistency model
  (eventual) and a different change rhythm than the operational write side. Keeping it apart means a heavy report can
  never slow down an assignment, and the two can be deployed and scaled independently.
- **Dependencies:** it references only `CrewCall.Contracts` (events and report shapes), `CrewCall.Messaging` (shared
  broker plumbing, extracted from CrewCall.Integrations in this sprint) and `CrewCall.ServiceDefaults`. It never
  references `CrewCall.Persistence` or a business module; an architecture test checks the full dependency closure.

### 2. It owns its database

- **Database:** `crewcall_reporting`, Aspire resource `crewcall-reporting`. Today it is a second logical database on the
  same PostgreSQL server; that is a deployment convenience, not a coupling.
- **Separate everything:** connection string, `ReportingDbContext` (not derived from CrewCallDbContext, sharing no
  entities), schema `reporting`, migration history and migrations (`InitialReportingProjections`).
- **Tables:**
  - `inbox_messages`
  - `technician_activity` (daily)
  - `visit_activity`
  - `assignment_activity`
  - `incident_activity`
  - `projection_checkpoints`

### 3. No operational database reads, no API calls

- Reporting never selects from `workorders.*`, `workforce.*`, `resources.*`, `scheduling.*` or `ops.*`, never joins
  across databases, and never calls CrewCall.Api to fill a gap.
- Its configuration has no operational connection string and no API address. A test reads the compiled assembly's
  strings to prove neither appears.
- **When an event lacks data a projection needs, the contract is fixed instead.** Sprint 14 added:
  - **`visit.created` v1:** VisitId, WorkOrderId, CustomerId, SiteId, planned start and end, CreatedAtUtc, work-order
    priority. To publish it, the operational `VisitCreated` payload now records the work order's customer, site and
    priority (operational payloads may evolve, ADR-0014).
  - **`visit.status-changed` v1:** VisitId, OldStatus, NewStatus, as strings, so the contract does not depend on the
    domain enum.
- The five existing v1 events are unchanged; nothing breaks for the audit or live consumers. A future breaking change
  would add a v2 next to v1 in `IntegrationEventCatalog`.

### 4. Event-driven projections

- **Explicit handlers:** one per event, no reflection —
  - `AssignmentCreatedProjectionHandler`
  - `AssignmentReplacedProjectionHandler`
  - `AssignmentCancelledProjectionHandler`
  - `IncidentDispatchedProjectionHandler`
  - `VisitCreatedProjectionHandler`
  - `VisitStatusChangedProjectionHandler`
  - `VisitWorkCompletedProjectionHandler`
- **Detail rows:** handlers change only visits, assignments and incidents.
- **Transport-independent:** the projection engine (`IReportingProjectionProcessor.ProcessAsync(envelope)`) does not
  know RabbitMQ. The consumer (`ReportingConsumer`) and any replay use the same processor.
- **Consumer mechanics:** reused from the shared `IntegrationEventConsumer` base — manual ACK after the commit, NACK
  with requeue on failure, reject invalid or poison messages, reconnect.
- **Queue:** `crewcall.reporting`, durable, bound to exactly the routing keys it projects:
  - `assignment.created`, `assignment.replaced`, `assignment.cancelled`;
  - `incident.dispatched`;
  - `visit.work.completed`, `visit.created`, `visit.status.changed`;
  - `visit.rescheduled` (Sprint 15, ADR-0017): `RescheduleCount` and `TotalDelayMinutes` on the visit, counted once
    per message thanks to the inbox. The planned window follows the latest plan by business time
    (`PlannedChangedAtUtc`), so a late `visit.created` never overwrites a move.
- **Not projected:** any other type or version is ACKed and ignored with a warning; nothing is recorded for it.

### 5. Eventual consistency

- Reports reflect the events projected so far. A change committed a moment ago may not be in them yet.
- Reporting does not pretend otherwise. Every response carries `Metadata`:
  - `GeneratedAtUtc`;
  - `ProjectionUpdatedAtUtc`: when the projections last applied an event;
  - `DataAsOfUtc`: the newest business time among the applied events.

### 6. Reporting inbox and idempotency

- **Inbox:** `reporting.inbox_messages`, keyed by `(consumer_name, message_id)`, with the consumer name
  `crewcall-reporting`. It is separate from `ops.inbox_messages`.
- **One transaction per message:**

  ```
  inbox record → projection handler → daily recompute → checkpoint → COMMIT → ACK
  ```

  - The inbox insert uses `ON CONFLICT DO NOTHING`. If the message was seen before, nothing changes and it is ACKed as
    a duplicate.
  - A failure anywhere rolls back everything, the inbox record included. The message is redelivered and projected from
    the start.
- **Advisory lock:** a transaction-scoped advisory lock serializes projection transactions, so two consumers or a
  consumer and a replay never recompute the same technician day from different snapshots.

### 7. Out-of-order delivery

RabbitMQ gives no global order (the outbox publishes oldest first per batch, but retries and several publishers reorder;
ADR-0014). The projections never assume an order.

**Late or partial rows**

- A row is started by whichever event about it arrives first and filled in by the others. Fields not delivered yet are
  null; nothing is dropped.
- Example: `visit.work.completed` before `assignment.created` gives a visit with its actual durations and no
  technician. The later assignment event attributes it.

**Attribution: one technician per visit**

- Derived from the visit's assignments as far as they are known: the assignment neither replaced nor cancelled.
- Among several such assignments, the newest wins; one whose creation has not arrived yet counts as the newest. This is
  the case of a replacement whose `assignment.replaced` has not arrived yet.
- The attribution is re-evaluated whenever an assignment or visit event arrives.

**Reassignment around a completion**

- The work counts once, for the technician of the current assignment: the end of the replacement chain.
- The replaced technician keeps the assignment in their count but not the completed work.

**Statuses**

- They follow business time: the latest `OccurredAtUtc` wins. Ties go to the further status (Completed or Cancelled
  over InProgress over Planned).
- **assignment.created after replaced or cancelled:** fills in the technician and keeps the end.
- **incident.dispatched:** fills in only what `assignment.created` has not told yet.

**Correction:** the affected technician days are recomputed (point 8).

**Proven by tests:**

- every one of the 24 orders of a reassignment around a completion gives the identical read model;
- the out-of-order completion case matches the in-order result.

### 8. Daily aggregate: recompute, not increment

- `technician_activity` holds one row per technician and UTC day:
  - completed visits;
  - assignments;
  - incident dispatches;
  - travel, gross work, pause and net work minutes.
- A month or a year report reads at most one row per technician and day, never the event history.
- **Rows are recomputed, never incremented.** After a message changed detail rows, every technician day those rows
  counted towards before or count towards now is recomputed from the detail projections. Days with nothing left are
  removed.
- A visit counts towards the day it was completed. An assignment counts towards the day it was created. An incident
  counts towards the day it was dispatched.
- **Why recompute:**
  - a duplicate, a redelivery or a correction (an attribution moving from "unknown" to A, or from A to B) can never
    count twice or be left behind;
  - the result depends only on the detail rows, which depend only on the set of events.
- **Cost:** a few indexed queries per affected day, which is small next to the guarantee.

### 9. Planned versus actual

- **Planned:** the visit report gives the planned window and `PlannedDurationMinutes`.
- **Actual:** the actual travel, gross work, pause and net work minutes.
- **Variance:** `VarianceMinutes = ActualNetWorkMinutes − PlannedDurationMinutes`. It is null when there is no actual
  yet.
- Both derived values are computed when the report is built and are never stored.
- Example: planned 09:00–10:00 (60 min), actual net 75 min, variance +15.

### 10. Utilization: definition and limits

- **Definition:** `OperationalUtilization = NetWorkMinutes / (GrossWorkMinutes + TravelMinutes)`, over the completed
  visits in the period, rounded to 4 decimals. It is the share of the time spent on completed visits (travel plus time
  on site) that was actual work.
- **No data is not zero:** when the denominator is zero, it is `null`.
- **Not payroll or HR utilization:** there is no data on contracted working time, working norms, absences in the
  denominator, idle time between visits, or work on visits that were never completed. Those need data Reporting does
  not have yet.

### 11. Replay and rebuild

- **Rebuild foundation:** `IReportingProjectionResetService.ResetAsync(includeInbox)`.
  - It truncates the projection tables and the checkpoint; migrations stay.
  - With `includeInbox` it also clears the reporting inbox, which is required before replaying the same messages
    (otherwise they are recognized as duplicates).
  - It is a development and test operation, not exposed over HTTP.
- **Replay:** goes through the same `IReportingProjectionProcessor` as live delivery.
- **Proven by tests:** a realistic history (three technicians, two days, replacement, cancellation, incident) projected
  from empty gives a byte-identical read model:
  - on every replay;
  - in reverse order;
  - with duplicates mixed in.

### 12. RabbitMQ is not the event store

- A queue is a buffer. Messages are removed once acknowledged, and broker retention is configuration, not history.
- Reporting cannot rebuild from RabbitMQ, and Sprint 14 does not pretend it can.
- The operational events (`ops.operational_events`) are append-only history, but they belong to the operational
  database, which Reporting must not read.

### 13. Future: Integration Event Replay Feed

A full historical rebuild needs a replay source that respects the boundary: an **Integration Event Replay Feed**.

- It would be an endpoint or stream owned by the operational side (or Integrations) that re-serves integration event
  envelopes for a time range, with their original MessageIds.
- Reporting would consume it through the same processor, under a fresh inbox (reset) or a dedicated consumer name.
- It is out of scope here. The processor and the reset service are the seams it will plug into.

### 14. Independent scaling

- **Consumers:** Reporting scales separately from the API and from Integrations. Several Reporting instances can consume
  `crewcall.reporting`; the inbox prevents double projection and the advisory lock prevents conflicting recomputes.
- **Write throughput:** the advisory lock serializes projection writes. If they ever become a bottleneck, the lock can
  be narrowed per technician.
- **Reads:** the Reporting API reads only its own database, so it can be scaled, or given read replicas, without
  touching the operational database.
- **Deployment:** the reporting database can move to its own server by changing one connection string.
- **Failure isolation:** if Reporting or its database is down, the operational side keeps working. Messages wait in the
  durable queue (subject to broker configuration) and the projections catch up when it returns. If CrewCall.Api is
  down, existing reports are still served.

## Consequences

- Reports are cheap to query and isolated from the write side. The price is eventual consistency, stated in every
  response.
- Projection semantics must stay order-independent; every new handler must keep the determinism tests passing.
- **UTC days:** a technician working in Warsaw past midnight UTC has work on two UTC days. Local payroll days need the
  historical time-zone context of each technician and are future work.
- **Partial rows:** a visit whose `visit.created` was never received (e.g. created before Sprint 14) keeps null planned
  fields; there is no back-fill without the replay feed.
- **Retention:** processed reporting inbox rows accumulate; a retention job is future work.
- **Out of scope:** payroll, billing, HR utilization, team history, BI or OLAP, Kafka, EventStoreDB, event sourcing,
  PDF or Excel exports, gRPC, Google Calendar, the Dispatch Board and authentication.
