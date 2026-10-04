# ADR-0004: Persistence and schema ownership

- **Status:** Accepted
- **Date:** 2026-10-04
- **Sprint:** 1A

## Context

CrewCall's bounded contexts (ADR-0001) need a persistence model that keeps data ownership explicit from the start,
without the cost of one database per module while everything still runs in one process (ADR-0003).

## Decision

1. **One physical PostgreSQL database** named `crewcall`, run locally by Aspire as the `crewcall-postgres` resource.
2. **One schema per owner:**

   | Schema | Owner |
   |---|---|
   | `workorders` | WorkOrders: customers, sites, work orders, incidents |
   | `workforce` | Workforce: technicians, teams, skills, working hours, absences |
   | `resources` | Resources: vehicles, equipment |
   | `scheduling` | Scheduling: visits, assignments, operational calendar |
   | `ops` | Operational and event infrastructure: operational history, outbox, EF migrations history |

   The names are defined once in `CrewCall.Persistence.DatabaseSchemas`. The baseline migration `InitialCrewCall`
   creates the schemas and nothing else.
3. **A module writes only to its own schema.** It does not query another module's tables. Data owned by another
   module is obtained through contracts (ADR-0001). Cross-schema foreign keys are avoided; references across
   modules are by identifier.
4. `CrewCallDbContext` lives in `CrewCall.Persistence` and has no entities yet. When modules add entities, each
   entity configuration states its schema explicitly. Splitting into per-module DbContexts on the same database is
   possible later without moving data, because ownership is already expressed by schema.
5. The connection string is supplied by configuration (by Aspire through the AppHost). The generated database
   password is stored in the AppHost user secrets, never in the repository.
6. In Development the API applies migrations on startup. A dedicated migration step replaces this before any
   shared environment exists.

## Consequences

- Data ownership is visible in the database itself, not only in code.
- Extracting a module to its own service (ADR-0003) means moving one schema, not untangling shared tables.
- Rule 3 is a convention in this sprint. It can later be enforced with per-module database roles or per-module
  DbContexts.
