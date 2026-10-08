# ADR-0014: Transactional outbox and RabbitMQ

- **Status:** Accepted
- **Date:** 2026-10-07
- **Sprint:** 12

## Context

CrewCall records many operational events (ADR-0006). Some of them matter to other processes: an assignment was
created, an incident was dispatched, field work was completed. We want to publish those reliably to a message broker
(RabbitMQ) without making the business transaction depend on the broker.

## Decision

### 1. Publishing directly after SaveChanges is wrong (dual write)

`SaveChanges(); channel.BasicPublish(...)` writes to two systems that do not share a transaction:

- a crash, timeout or broker outage between the two loses the message although the state was committed;
- publishing first and then failing to commit announces something that never happened.

Neither ordering is safe, and retries cannot fix it, because the second write may never run.

### 2. Transactional outbox

- A published event is first written as a row in `ops.outbox_messages`, by the same `SaveChanges`, in the same
  transaction as the business change.
- It is not a second save and not a separate step: `CrewCallDbContext.AppendOperationalEvent`, which every module
  already calls inside its unit of work, also adds the outbox message (`IOutboxWriter`) when the event is published.
- Business state, operational event and outbox message therefore commit together or not at all:
  - a rollback (a lost claim race, a failed final check) removes the outbox message;
  - a failing outbox insert rolls back the business change.
- A separate process publishes the outbox afterwards.

```
Business Operation
      |
      v
PostgreSQL Transaction
      |
      +--> domain state
      +--> operational event
      +--> outbox message
      |
    COMMIT
      |
      v
Outbox Publisher        (CrewCall.Integrations)
      |
      v
RabbitMQ                (crewcall.events, topic)
      |
      v
Consumer                (crewcall.integration-audit)
      |
      v
Inbox / Idempotency     (ops.inbox_messages)
      |
      v
Consumer Effect         (ops.integration_event_receipts)
```

### 3. OperationalEvent != IntegrationEvent

| | Operational event | Integration event |
|---|---|---|
| Purpose | the application's own detailed history | a contract between processes |
| Stored in | `ops.operational_events` | `ops.outbox_messages` |
| Change policy | may change with the application | stable and versioned |
| Name | internal | stable `Type` (`visit.work-completed`), never a .NET type name |
| Version | none | `Version` (1) |
| Content | detailed payload | small explicit contract (`CrewCall.Contracts.Integration`), never an EF entity |

`IntegrationEventMapper` is the explicit table from operational to integration events: a `switch` on the event type and
payload, with no reflection (a static class since Sprint 15, ADR-0017 §11). Sprint 12 published only these five events;
Sprint 14 added `visit.created` and `visit.status-changed` (ADR-0016), and Sprint 15 `visit.rescheduled` (ADR-0017):

| Type (v1) | Routing key |
|---|---|
| `assignment.created` | `assignment.created` |
| `assignment.replaced` | `assignment.replaced` |
| `assignment.cancelled` | `assignment.cancelled` |
| `incident.dispatched` | `incident.dispatched` |
| `visit.work-completed` | `visit.work.completed` |

All other operational events stay internal. The operational payload JSON is never forwarded 1:1.

### 4. At-least-once delivery

- The outbox guarantees that every committed event is eventually published, and that nothing uncommitted is.
- It does not guarantee exactly once: a message can reach the broker more than once (point 8).
- Exactly-once is not attempted. Consumers must tolerate duplicates (point 7).

### 5. Publisher confirms

- The publisher's channel has confirmations enabled and tracked (RabbitMQ.Client 7), so `BasicPublishAsync` returns
  only after the broker acknowledged the message. It throws on a nack, a closed channel or a timeout.
- Only then is the message marked `ProcessedAtUtc`. Publishing alone is never success.
- Messages are persistent (delivery mode 2) on a durable exchange.

### 6. Duplicate delivery

Duplicates come from:

- a republish after a crash (point 8);
- a lease that expired while a slow publisher still finished;
- a consumer redelivery after a NACK or a lost connection.

All of them carry the same `MessageId`, which is the outbox id (envelope `MessageId` and AMQP `message-id`).

### 7. Inbox idempotency

- The consumer writes its inbox record (`ops.inbox_messages`, keyed by `MessageId`) and its effect in one transaction:
  `INSERT … ON CONFLICT (message_id) DO NOTHING`.
- If nothing was inserted, the message was handled before, and the effect is skipped. Two deliveries handled at the
  same moment serialize on the key, so the second sees the first.
- The audit effect, `ops.integration_event_receipts`, also has a unique `message_id` as a second guard.
- **Manual ACK:** the consumer acknowledges a message only after that transaction committed, or after recognizing a
  duplicate.
  - A transient failure (e.g. the database) NACKs with requeue after a short pause.
  - A body that is not a valid envelope is rejected without requeue, because it can never succeed.

### 8. The crash window after a confirm

The broker confirmed a message, and the publisher crashed before it committed `ProcessedAtUtc`. After the lease expires
another publisher claims the message and publishes it again, with the same MessageId. This is correct for
at-least-once delivery, and the consumer's inbox neutralizes the duplicate.

### 9. RabbitMQ topic exchange

- One durable topic exchange, `crewcall.events`. The routing key of each event is explicit in
  `IntegrationEventCatalog`.
- The technical consumer's durable queue `crewcall.integration-audit` is bound with `#` (everything).
- Future consumers bind their own queues with their own patterns, e.g. `visit.#`.

**Envelope** (`application/json`):

- `messageId`, `type`, `version`, `occurredAtUtc`, `correlationId`, `payload`;
- AMQP properties repeat MessageId, Type, CorrelationId, a timestamp and an `x-event-version` header.

**Correlation:** the API takes `X-Correlation-Id` (a GUID) from a request and writes it to the operational event, the
outbox message and the envelope.

### 10. RabbitMQ.Client, not MassTransit

- We want the mechanics to be explicit and owned: outbox claiming, confirms, ACK semantics and idempotency.
- MassTransit would hide most of that, brings licensing questions for newer versions, and offers abstractions we do not
  need.
- `RabbitMQ.Client` is used directly, without building a general messaging framework. It is used only by the
  messaging services: `CrewCall.Integrations` and, since Sprint 14, `CrewCall.Reporting`, through the small shared
  `CrewCall.Messaging` library (ADR-0016). It is never used by business modules or the API.

### 11. Multiple publishers

Several `CrewCall.Integrations` instances may run at once. No in-memory lock is used.

**Claim:** one short statement:

```sql
UPDATE … SET locked_until_utc = now + lease, locked_by = <publisher>
WHERE id IN (SELECT … WHERE pending ORDER BY created_at_utc, id LIMIT <batch> FOR UPDATE SKIP LOCKED)
RETURNING *
```

- `SKIP LOCKED` lets concurrent claimers take different rows without waiting for each other.
- The lease keeps a claimed message away from other publishers after the statement committed.

**Publish:**

- No database transaction is open while messages travel to the broker.
- Each result (processed, or failed with the error) is recorded in its own short statement.
- A publisher that dies leaves leases that expire, and the messages are claimed again.

**Pending** means: not processed, not dead-lettered, past its backoff, and not leased or the lease expired.

**Batches:**

- up to 50 messages, oldest first (`CreatedAtUtc`, `Id`);
- a partial index covers pending messages only;
- the publisher polls with a `PeriodicTimer`, every second by default. There is no LISTEN/NOTIFY.

**Retry:**

- A failure increments `AttemptCount`, records `LastError` and `LastAttemptAtUtc`, and sets `NextAttemptAtUtc` with an
  exponential backoff (2 s, doubling, at most 5 min).
- The rest of the batch is released unattempted, so a broker outage costs one attempt per pass, not one per message.
- After `MaxAttempts` (10) a message is dead-lettered: `FailedAtUtc` is set, `ProcessedAtUtc` stays null, and the
  message is kept and no longer retried automatically.
- All limits are configuration (`Outbox` section), not database data.

### 12. When the broker is unavailable

- The business operation does not touch the broker, so it commits as usual. The API returns its normal result, and the
  message waits in the outbox.
- The publisher records failed attempts with backoff. When the broker is back, the message is published, still with
  the same MessageId.
- The API's health does not include RabbitMQ. A broker or publisher problem must not take the API out of rotation while
  it can still safely write the outbox.
- RabbitMQ health is part of `CrewCall.Integrations`' readiness (`/health`: database and broker); its liveness
  (`/alive`) is the process only.
- If the database is down, nothing commits and nothing is published: the publisher can only publish committed outbox
  rows.

## Consequences

- No dual write: events can be published reliably while the broker is optional for the write side.
- Consumers must be idempotent. The inbox gives each consumer that foundation.
- Ordering is per publisher and batch (oldest first); global ordering across publishers or retries is not guaranteed.
  Consumers should not depend on it.
- Processed outbox rows and inbox rows accumulate. A retention job is future work.
- The inbox was keyed by MessageId alone while there was one consumer. Sprint 13 (ADR-0015) keys it by consumer name
  and MessageId, so each consumer handles a message once, independently of the others.
- Out of scope: SignalR, a reporting projection, gRPC, Kafka, MassTransit, event sourcing, sagas and exactly-once
  delivery.
