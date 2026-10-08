# ADR-0015: SignalR live operations

- **Status:** Accepted
- **Date:** 2026-10-08
- **Sprint:** 13

## Context

Dispatchers will need a screen that updates as work happens: an assignment created or replaced, an incident
dispatched, field work completed. Sprint 12 (ADR-0014) already publishes these changes reliably as integration events
on RabbitMQ (`crewcall.events`). Sprint 13 delivers them to browsers over SignalR, and prepares the backend for a future
Dispatch Board. The Dispatch Board itself is not part of this decision.

## Decision

```
Business Change
     |
     v
Outbox                       (ops.outbox_messages, same transaction as the change)
     |
     v
RabbitMQ                     (crewcall.events, topic)
     |
     +------------------+
     |                  |
     v                  v
Audit Consumer     Live Consumer           (crewcall.integration-audit / crewcall.live-operations)
     |                  |
     v                  v
Inbox             Live Inbox               (ops.inbox_messages, one record per consumer + MessageId)
                        |
                        v
                     SignalR               (/hubs/live-operations, client method ReceiveOperation)
                        |
             +----------+----------+-----------+
             |          |          |           |
            ALL       SITE     TECHNICIAN   INCIDENT
                        |
                        v
                       WEB                 (CrewCall.Web, /live)
```

### 1. SignalR is delivery, not the source of truth

- A live message says "something changed": what, where, and when. It is a change hint.
- The current state always comes from the REST API and its read models (point 8).
- SignalR keeps no history and no state. The hub has no business logic.
- A client that was offline may have missed messages, and they are not replayed.

### 2. RabbitMQ is the source of live notifications

- Live messages are made only from integration events consumed from RabbitMQ. Those events come from the
  transactional outbox, so a browser is told only about committed changes.
- Nothing calls SignalR directly from a request or a business transaction.
- If RabbitMQ is down:
  - business writes still commit (the outbox waits, ADR-0014);
  - the API and the Web keep running;
  - no live update arrives until the broker is back. Then the live consumer reconnects by itself and receives the
    events.

### 3. Business modules never call SignalR

- Only the delivery layer knows SignalR:
  - `CrewCall.Integrations` hosts the hub, the live consumer and `SignalRLiveOperationsPublisher`, which uses
    `IHubContext<LiveOperationsHub>`;
  - `CrewCall.Web` is a client.
- The live consumer depends on `ILiveOperationsPublisher`, not on the hub.
- An architecture test checks that WorkOrders, Workforce, Resources, Scheduling, Scheduling.Core, Persistence, Contracts
  and the API reference no SignalR assembly, and that no type in them depends on a SignalR type such as `IHubContext`.

**Hub placement.** The hub is hosted by `CrewCall.Integrations`, not by `CrewCall.Api`:

- `IHubContext` only reaches connections of its own process. The consumer that calls it must run where the hub runs,
  unless a backplane is added, and a backplane is out of scope.
- The consumer needs `RabbitMQ.Client`, which ADR-0014 keeps out of the API (the API only writes the outbox, and an
  architecture test checks that it does not ship the broker client).
- So the consumer and the hub both live in the delivery service, and the API stays free of broker and push concerns.
- If the hub must move to the API later, the consumer moves with it (and ADR-0014's rule is revisited), or a backplane is
  introduced.

### 4. The live consumer has its own inbox identity

- `ops.inbox_messages` is keyed by `(consumer_name, message_id)`. The audit consumer is `crewcall-integration-audit`;
  the live consumer is `crewcall-live-operations` (configuration `LiveOperations:ConsumerName`).
- Migration `AddPerConsumerInbox` keeps the existing records as the audit consumer's.
- The live consumer's effect is a SignalR broadcast, which cannot share a database transaction. So its flow is:

  ```
  validate envelope → inbox check (consumer, MessageId) → map → SignalR broadcast → inbox record → ACK
  ```

- **ACK** only after the broadcast succeeded and the inbox record was written.
- A broadcast or database failure throws; the message is NACKed with requeue and delivered again.
- An invalid envelope, or a payload that does not match its contract or whose `eventId` is not the MessageId, is
  rejected without requeue.
- A type or version without a live mapping (e.g. a future v2) is ACKed and ignored on purpose, with a warning.
- Broadcasting to groups nobody joined is a success: no retry just because nobody is connected.

### 5. Several consumers may handle the same MessageId independently

- The audit consumer and the live consumer each receive every relevant message on their own queue, and each records it
  under its own name.
- Neither blocks, skips nor resets the other.
- The same consumer never handles a MessageId twice in the normal case.

### 6. Groups and routing

**Groups.** One hub (`/hubs/live-operations`) and server-built group names:

- `all`
- `site:{siteId}`
- `technician:{technicianId}`
- `incident:{incidentId}`

The ids are lower-case hyphenated GUIDs, so one id always gives one group name.

**Hub methods:**

- `SubscribeAll()`, `SubscribeSite(Guid)`, `SubscribeTechnician(Guid)`, `SubscribeIncident(Guid)`;
- the matching `Unsubscribe…` methods.

No method takes a group name or any string: a client cannot join an arbitrary group. An empty GUID is refused with a
`HubException`.

**Client method.** One method, `ReceiveOperation(LiveOperationMessage)`, for every type.

**Message.** `LiveOperationMessage` (`CrewCall.Contracts.Live`) is separate from the integration contracts, and small:

- MessageId, Type, EntityId, OccurredAtUtc, CorrelationId.

It means "entity X changed" and never carries entities, calendars or history. *(Simplified in Sprint 15, ADR-0017 §10:
EntityType, Action, `Related` and Summary were removed; the type names the entity and the change.)*

**Mapping.** `LiveOperationMapper` is the explicit table:

| Integration event (v1) | Live type | EntityId | Groups |
|---|---|---|---|
| `assignment.created` | `assignment.created` | assignment | all, technician, site |
| `assignment.replaced` | `assignment.replaced` | new assignment | all, old and new technician, site |
| `assignment.cancelled` | `assignment.cancelled` | assignment | all, technician, site |
| `incident.dispatched` | `incident.dispatched` | incident | all, incident, technician, site |
| `visit.work-completed` | `visit.work.completed` | visit | all, technician of the active assignment, site |
| `visit.rescheduled` *(Sprint 15)* | `visit.rescheduled` | visit | all, incident, technician of the active assignment, site |

**Queue.** The live queue `crewcall.live-operations` is bound to exactly these six routing keys.

**Routing context.** The site, and technicians known only by assignment id, are not in the v1 events. The v1 contracts
stay unchanged; instead there is a minimal read-only lookup:

- the visit's site comes through its work order;
- technicians come from the assignment rows.

The lookup runs once per message, never per connection. A missing row only means fewer groups.

**Duplicates across groups.** A connection in several matching groups (e.g. `all` and its site) receives the message
once per group. It deduplicates by MessageId (point 9).

### 7. Reconnect strategy

- The Web client (`LiveOperationsClient`) uses `WithAutomaticReconnect` and shows Connected, Reconnecting or
  Disconnected.
- A reconnect is a new server connection without groups. The client remembers its subscriptions and joins them again
  in `Reconnected`, before reporting Connected.
- If the first connection fails, the page shows Disconnected and offers to connect.
- The hub address is not hard-coded:
  - `LiveOperations:HubUrl` when set;
  - otherwise Aspire service discovery for `crewcall-integrations` (HTTPS preferred) plus `/hubs/live-operations`.

### 8. REST refresh after a live event

- A screen does not build business state from live messages. On a message, it refreshes the affected read model (the
  operational calendar, the visit, the incident) from the REST API.
- After a reconnect it refreshes what it shows, because messages may have been missed.
- The `/live` developer page only lists the messages, which is what it is for.

### 9. Duplicate delivery and client MessageId deduplication

- Delivery stays at-least-once.
- The broadcast and the inbox record are two steps. If the process dies after the broadcast and before the inbox
  record, the redelivered message is broadcast again with the same MessageId (the outbox id).
- Exactly-once SignalR delivery is not attempted. Every client deduplicates by MessageId. The Web client remembers the
  last 1000 ids and shows the last 50 messages.

### 10. No live-event persistence table

- Live messages are not stored. The source of truth is the operational data and the integration pipeline (outbox,
  inbox, receipts).
- A client that missed messages refreshes through REST.

### 11. Future authorization requirements

There is no authentication yet, and none is built for SignalR alone. Today any client that reaches the hub may
subscribe to any site, technician or incident. Before real use:

- the hub requires an authenticated user;
- each `Subscribe…` checks that the user may see that site, technician or incident (the server-built group names
  already give one place to do it);
- `all` becomes a dispatcher-only group.

## Consequences

- Browsers learn about committed changes within moments, with no polling and no change to the business modules.
- SignalR failures and broker outages never affect business writes. They only delay notifications, and the REST API
  remains correct.
- **Scale-out:** the hub runs in-process in `CrewCall.Integrations`. With more than one instance, each instance's live
  consumer would broadcast only to its own connections, and the queue would split messages between instances. That needs
  a backplane or a per-instance queue, which is out of scope (no Redis backplane, no Azure SignalR Service).
- **Stale backlog:** the live queue is durable. Messages that wait while the delivery service is down are broadcast late
  when it comes back. They are still correct as change hints, and an expiry for them is future work.
- **Out of scope:** the Dispatch Board, maps or GPS, authentication, Reporting, gRPC, Google Calendar, event sourcing.
