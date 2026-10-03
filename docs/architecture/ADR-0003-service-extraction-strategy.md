# ADR-0003: Service extraction strategy

- **Status:** Accepted
- **Date:** 2026-10-03
- **Sprint:** 1

## Context

CrewCall is intended to demonstrate a distributed system (.NET Aspire, RabbitMQ, gRPC, SignalR). A common failure
mode is to start with microservices before the domain boundaries are understood, or to start with an unstructured
monolith and then rebuild it twice: first into a modular monolith, then into services.

## Decision

**Modular architecture first. Service extraction when justified.**

1. Every bounded context starts as a module (a class library) composed in-process by `CrewCall.Api`.
2. Module boundaries are kept strict from the start (see [ADR-0001](ADR-0001-module-boundaries.md)): no direct
   module-to-module references, contracts in `CrewCall.Contracts`, no transport types in contracts.
3. A module is extracted into a separate process only when it meets one or more of these criteria, recorded in a
   dedicated ADR at extraction time:
   - **independent lifecycle:** it changes and is released on a different cadence,
   - **separate deployment need:** it must be deployed or rolled back independently,
   - **independent failure:** its failure must not take down the core system (or the reverse),
   - **different scaling profile:** its load or resource usage differs significantly,
   - **clear data ownership:** it owns its data and others need only events or queries,
   - **justified service-to-service communication:** the resulting calls (gRPC) or events (RabbitMQ) carry real
     value and are not just chatty replacements for in-process calls.

Because the module keeps its model and its contract, extraction looks like this:

```
today:  CrewCall.Api -> CrewCall.Workforce                         (in-process)
later:  CrewCall.Api -> gRPC / RabbitMQ -> CrewCall.Workforce.Service (same model, new host)
```

No proxies, network abstractions, gRPC or messaging infrastructure are built before they are used.

### Candidates

- **Natural future candidates: Integrations, Reporting.**
  - *Integrations:* external calendar APIs fail independently. CrewCall must keep working, and sync must be
    retryable later.
  - *Reporting:* a different load profile and read models built asynchronously from operational events.
- **Possible later candidates: Workforce, Resources, Scheduling.** These are extracted only if the criteria above
  are met. Scheduling needs consistent reads of availability and resources for conflict detection, which argues
  for keeping it close to its data.

This ADR does **not** declare that every module will become a microservice.

## Consequences

- The system is simple to run and debug while the domain is being built.
- Extraction is a hosting and transport change, not a domain rewrite.
- Each extraction has to be argued in an ADR, which keeps the distributed parts of the system honest and
  explainable.
