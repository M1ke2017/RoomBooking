# ADR-0008: Scheduling check vs resource claim (CHECK != COMMIT)

- **Status:** Accepted
- **Date:** 2026-10-04
- **Sprint:** 6B

## Context

Sprint 6B adds conflict detection: can a technician, an optional vehicle and some equipment assets take a visit in
`[Start, End)` without clashing with the existing plan? Visits do not reference resources yet, and assignments do not
exist. Conflict detection still needs something to detect conflicts against. It also has to be honest about what a
"yes" means when other dispatchers plan at the same time.

## Decision

1. **Resource occupation is a technical model, `scheduling.resource_reservations`**: `id`, `resource_type`
   (`Technician` | `Vehicle` | `Equipment`), `resource_id`, `visit_id` (nullable), `start_at`, `end_at`.
   - Every technician, vehicle and equipment asset is its own resource. Equipment is not a quantity pool.
   - A reservation is not an assignment. It only records that a resource is occupied.
   - `resource_id` and `visit_id` have no foreign keys: they point into other modules' schemas, and `resource_id` can
     point to three different tables.
2. **The scheduling check is advisory and read-only.**
   - `POST /api/scheduling/check` (`SchedulingCheckService`) writes nothing. It returns 200 with `IsFeasible` and
     every reason, so an infeasible answer is still a successful evaluation.
   - It never returns 409, because nothing was attempted. It returns 400 for an invalid request and 404 for a
     missing technician, vehicle or equipment asset.
3. **A "feasible" answer is a snapshot, not a promise.** Another request can reserve the same resource a moment
   later. The check does not lock, hold or claim anything, and that is intended.
4. **The final guard is PostgreSQL.** The constraint
   `EXCLUDE USING gist (resource_type WITH =, resource_id WITH =, tstzrange(start_at, end_at, '[)') WITH &&)`
   rejects two overlapping reservations of the same resource, whatever code path writes them. Touching ranges are
   allowed.
5. **Claiming is a later, atomic step.** Assignment (Sprint 7) will:
   - write the reservations for all of a visit's resources in one transaction,
   - rely on the exclusion constraint to reject a resource that was taken after the check,
   - report that as a conflict (409 at that point, because a write was attempted).

   `ResourceReservationService.CreateAsync` already pre-checks and maps the constraint violation to an `Overlaps`
   outcome. Sprint 6B uses it only for test fixtures.
6. **Travel buffer.**
   - The check compares reservations against `[Start - before, End + after)`, where the buffers come from the
     request (0–480 minutes, default 0, overflow-safe).
   - A reservation overlapping the visit itself is a `*ReservationConflict`. One overlapping only the buffer is a
     `TravelBufferConflict`.
   - Workforce availability is checked for the visit itself, not for the buffer.
   - Reservations store the occupied period as given. Whether Assignment includes buffers in what it stores is
     decided in Sprint 7.
7. **Self-exclusion.** With `VisitId`, that visit's own reservations are ignored, so re-planning a visit does not
   conflict with itself.
8. **Decisions in F#, orchestration in C#, modules through ports.**
   - Overlap, touching, buffer classification, self-exclusion and ordering are the pure
     `Scheduling.Core.ResourceConflicts.detect`. Technician feasibility (active, skills, availability) is
     `Feasibility.evaluateCandidate`.
   - `CrewCall.Scheduling` orchestrates and maps results. Following ADR-0001, it does not reference Workforce or
     Resources. It declares ports (`ITechnicianSchedulingSource`, `IResourceCatalog`), and the composition root
     (`CrewCall.Api/SchedulingAdapters`) implements them.
   - Workforce remains the only owner of working hours, absences, holidays and time zones. The adapter passes on
     `WorkforceAvailabilityService`'s decision unchanged.

## Consequences

- Dispatchers get a complete, typed list of problems (inactive, unavailable, missing skills, each reserved resource,
  buffer clashes) in one call, without side effects.
- Double booking is impossible at the database level even before Assignment exists.
- The race between check and claim is explicit. Clients must treat a later claim conflict as normal.
- Vehicles and equipment have no availability calendars yet, so only reservations are checked for them.
