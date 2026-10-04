# ADR-0005: Module data access through module-owned DbContext interfaces

- **Status:** Accepted
- **Date:** 2026-10-04
- **Sprint:** 2

## Context

Sprint 2 adds the first entities: Customer and Site (WorkOrders) and Technician (Workforce). Their services need
the database. ADR-0004 establishes one physical database with one schema per module and the rule that a module
does not query another module's tables.

`CrewCallDbContext` lives in `CrewCall.Persistence` and must know the entity types to map them. If the modules
referenced Persistence to use that context, Persistence could not reference the modules: a dependency cycle.

## Decision

1. Each module declares the data it owns as a narrow interface: `IWorkOrdersDbContext` (`Customers`, `Sites`) and
   `IWorkforceDbContext` (`Technicians`), each with `SaveChangesAsync`.
2. `CrewCallDbContext` implements all module interfaces. `CrewCall.Persistence` references the modules, maps their
   entities (`IEntityTypeConfiguration<T>` per entity, in `Persistence/Configurations`) and registers each
   interface against the same scoped context.
3. Module services use EF Core directly through their interface (`DbSet<T>`, LINQ, `SaveChangesAsync`).
   There is no repository or unit-of-work abstraction on top of EF Core.
4. Modules reference only `Microsoft.EntityFrameworkCore` (for `DbSet<T>`), never Persistence, Npgsql or ASP.NET Core.
5. Modules report results as explicit outcome types (for example `CreateSiteOutcome.CustomerNotFound`). Exceptions are
   not used for expected business results. The API maps outcomes to HTTP status codes.
6. API request/response models live in `CrewCall.Contracts`. The API maps them to module commands and module
   entities to responses, so EF entities are never serialized directly.

## Consequences

- The dependency direction is infrastructure → domain: `Api → Persistence → WorkOrders / Workforce`. No cycles.
- The schema ownership rule of ADR-0004 is enforced by the compiler: WorkOrders cannot see `Technicians`, and
  Workforce cannot see `Customers` or `Sites`.
- All modules still share one `DbContext` instance per request, so a request that touches two modules can save
  atomically if that is ever needed. Splitting into per-module DbContexts later only changes Persistence.
- Extracting a module to its own service (ADR-0003) means moving its interface implementation, its configurations
  and its schema, not rewriting its services.
