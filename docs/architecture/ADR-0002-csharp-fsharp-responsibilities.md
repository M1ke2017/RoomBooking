# ADR-0002: C# and F# responsibilities

- **Status:** Accepted
- **Date:** 2026-10-03
- **Sprint:** 1

## Context

RoomBooking used F# for HTTP hosting and API endpoints and C# for EF Core persistence. The only business rule,
reservation conflict detection, was an inline LINQ query inside an endpoint handler. No pure, testable business
logic existed.

CrewCall's hardest problems are decision problems: is a technician available, does an assignment conflict, which
technician and vehicle best match an urgent incident, what should be moved to make room. These rules benefit from
immutable data, discriminated unions, exhaustive pattern matching and functions that are trivial to test.
The surrounding I/O (HTTP, database, messaging, hosting, third-party SDKs) is best served by the mainstream C#
ecosystem and tooling.

## Decision

**C# = I/O and infrastructure.** C# is responsible for:

- API (REST host, composition root),
- orchestration (loading state, calling decision logic, persisting results, publishing events),
- persistence (EF Core, PostgreSQL),
- integrations (external calendars and other third-party systems),
- messaging,
- hosting.

**F# = decision logic.** F# (`CrewCall.Scheduling.Core`) is responsible for pure business logic:

- scheduling rules,
- conflict detection,
- availability evaluation,
- assignment validation,
- matching,
- scoring,
- rescheduling decisions.

Rules for `CrewCall.Scheduling.Core`:

1. Pure functions over immutable inputs. No I/O, no clock, no randomness. Time and IDs are passed in.
2. No references to ASP.NET Core, EF Core, PostgreSQL, RabbitMQ, gRPC, SignalR or HttpClient, and no references to
   other CrewCall projects.
3. Inputs and outputs are F# domain types. Mapping between persistence/contract types and F# types is done in C#
   (`CrewCall.Scheduling`).
4. Decisions are returned as values (results, proposals, conflicts). The C# caller decides what to persist or publish.

## Consequences

- Business rules can be unit- and property-tested without a database or web host.
- The F#/C# boundary needs explicit mapping (options, discriminated unions, collections). The mapping lives in
  `CrewCall.Scheduling`, and F# types are not exposed directly through the API or persisted with EF Core.
- The rest of the system stays in the mainstream C# stack, which keeps hosting, integrations and onboarding simple.
- Because `net10.0` exposes the whole BCL (including `System.Net.Http`), rule 2 cannot be fully enforced by project
  references alone. It is enforced by review now, and later by an architecture test.
