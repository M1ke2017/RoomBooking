# ADR-0001: Module boundaries

- **Status:** Accepted
- **Date:** 2026-10-03
- **Sprint:** 1

## Context

CrewCall evolved from an earlier RoomBooking project into a distributed field-operations coordination system.
RoomBooking was organised around technical layers (`BookingService`, `IntegrationService`, a shared
`Infrastructure` type library, a `Migrations` project) and around a single aggregate (`Reservation` of a `Room`).
That structure does not describe the CrewCall domain: technicians, teams, customers, sites, vehicles, equipment,
work orders, visits, incidents and the schedule that ties them together.

We want boundaries that follow the domain from day one, so that later evolution (separate processes, messaging,
gRPC) does not require re-modelling.

## Decision

CrewCall is divided into the following bounded contexts:

| Context | Owns | Project (Sprint 1) |
|---|---|---|
| **WorkOrders** | Customers, Sites, WorkOrders, Incidents: *what* has to be done, *for whom*, *where*, with what priority | `CrewCall.WorkOrders` |
| **Workforce** | Technicians, Teams, Skills, working hours, absences, workforce availability: *who* can do the work | `CrewCall.Workforce` |
| **Resources** | Vehicles, Equipment, resource availability: *with what* the work is done | `CrewCall.Resources` |
| **Scheduling** | Visits, Assignments, the operational calendar, conflict detection, matching, rescheduling: *when* and *by whom* | `CrewCall.Scheduling` (orchestration, C#) + `CrewCall.Scheduling.Core` (decisions, F#) |
| **Integrations** | Synchronisation with external systems (Google Calendar, Outlook, ...). External calendars are integration targets, never the system of record | *not created yet* |
| **Reporting** | Read models and reports built from operational history | *not created yet* |

Supporting projects:

- `CrewCall.Api`: REST host and composition root. Composes the modules in-process.
- `CrewCall.Contracts`: transport-agnostic contracts (events, requests, responses) that cross module boundaries.
  No transport-specific names (`RabbitMq...`, `Grpc...`).
- `CrewCall.Persistence`: EF Core / PostgreSQL infrastructure (empty in Sprint 1).
- `CrewCall.Web`: Blazor WebAssembly client. Talks to the API over HTTP only.

Integrations and Reporting are deliberately **not** created as projects yet. They will appear when the first real
process (calendar sync, report projection) justifies them.

### Dependency rules

```
CrewCall.Api        -> WorkOrders, Workforce, Resources, Scheduling
CrewCall.Scheduling -> Scheduling.Core, Contracts
other modules       -> Contracts (only when actually needed)
```

Forbidden:

- `Scheduling.Core` -> anything infrastructural (Api, Persistence, ASP.NET Core, EF Core, PostgreSQL, RabbitMQ,
  gRPC, SignalR, HttpClient) or any other CrewCall project.
- Any module -> `CrewCall.Api` or `CrewCall.Web`.
- Modules referencing each other directly. Cross-module communication goes through `Contracts`.

### Bounded context != microservice

A bounded context is a **model and ownership boundary**, not a deployment unit. All contexts currently run inside
one process (`CrewCall.Api`). Whether any of them becomes a separate service is decided per context, using the
criteria in [ADR-0003](ADR-0003-service-extraction-strategy.md).

## Consequences

- Each module can evolve its model independently, and ownership of data is explicit.
- Scheduling, the core of the product, depends on the other contexts only through contracts. That keeps it
  replaceable and testable.
- Some duplication of identifiers and read data across modules is expected and accepted.
- Dependency rules are enforced by review in Sprint 1. An automated architecture test should be added once a
  test project exists.
