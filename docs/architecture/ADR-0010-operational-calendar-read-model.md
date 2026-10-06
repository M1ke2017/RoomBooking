# ADR-0010: Operational calendar as a read model

- **Status:** Accepted
- **Date:** 2026-10-04
- **Sprint:** 8

## Context

Dispatchers need one view of the operation for a day, a week or a month. The questions it answers:

- what happens when, for which customer, at which site, and for which work order;
- who works on it, in which team, with which vehicle and equipment;
- what state the visit and its assignment are in.

The data is owned by four modules (WorkOrders, Workforce, Resources, Scheduling). No module can answer this alone, and
none should take on a dependency on the others to do so (ADR-0001).

## Decision

1. **The operational calendar is a read model.**
   - `OperationalCalendarService` (`CrewCall.Persistence.ReadModels.OperationalCalendar`) composes visits, work
     orders, customers, sites, current assignments, technicians, teams, vehicles and equipment into one projection.
   - It has no write logic and copies no domain rules. It only joins what the modules have stored.
   - All queries are `AsNoTracking`.
   - It lives next to `CrewCallDbContext` because that is the one place that already maps every module's tables.
     Module code is not changed and modules still do not see each other's data.
2. **One endpoint, half-open range.**
   - `GET /api/operational-calendar?start&end&timeZoneId&perspective&perspectiveId` serves day, week and month views.
     The range may be at most 31 days.
   - A visit belongs to the range when it overlaps it: `visit.start < range.end AND visit.end > range.start`. A visit
     that only touches the range is excluded.
   - Results are ordered by visit start, end, then id.
3. **Times are returned in UTC and in the requested zone.**
   - `timeZoneId` is an IANA id from the query. The service never infers it from a technician.
   - Every time is returned twice: in UTC and as local time with the offset in effect at that instant. This uses the
     same NodaTime TZDB as Workforce, so DST is applied per instant.
4. **Current assignment only.**
   - Each visit carries its *active* assignment, or nulls when it is unassigned. Replaced and cancelled assignments are
     history, available from `GET /api/visits/{visitId}/assignments/history` (Sprint 7).
5. **Perspectives:**

   | Perspective | Visits returned | Unassigned visits |
   |---|---|---|
   | All | every visit in the range | included |
   | Site | the visits of one site's work orders | included |
   | Technician | visits whose active assignment names that technician | excluded |
   | Team | visits whose assigned technician is a member of the team | excluded |
   | Vehicle | visits whose active assignment uses that vehicle | excluded |

   A missing perspective entity returns 404.
6. **Team means *current* membership.** Team membership has no history yet, so a visit is shown under the team its
   technician belongs to *now*. When a technician moves teams, their visits, past ones included, move with them.
   Historical team views need a membership history first.
7. **Completed and cancelled visits are returned.** The calendar is operational history, not only the future plan.
   Each item carries `VisitStatus`, and the UI decides how to show it.
8. **Query strategy (no N+1).** The number of queries is constant:
   1. at most one existence query for the perspective's entity;
   2. one query for the overlapping visits, joined 1:1 with work order, customer and site, and left-joined with the
      active assignment (at most one per visit by the partial unique index), its technician, the technician's team
      and the vehicle;
   3. one query for the equipment of the returned assignments. This relation is 1:N and is kept separate, so the main
      query cannot multiply rows.

   A test counts the SQL commands and requires the same count for 1 and 12 visits.

## Consequences

- The UI (later sprints) gets complete, ready-to-render items from one call.
- The read model reads every module's tables directly. That is acceptable for a read side. It must change together with
  the mapped entities, and it is covered by end-to-end tests.
- **Extraction to Reporting.** The service is self-contained: one class, its query and its DTOs. It can move to a
  future Reporting context, with its own projection tables fed by operational events, without changing the API
  contract.
