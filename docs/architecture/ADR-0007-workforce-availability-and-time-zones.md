# ADR-0007: Workforce availability and time zones

- **Status:** Accepted
- **Date:** 2026-10-04
- **Sprint:** 5

## Context

Scheduling will need to know whether a technician can work during an interval. Field technicians work local
wall-clock hours ("Monday 08:00–16:00"), while visits and absences are moments in time. The two only line up through
the technician's time zone, and the mapping changes twice a year with daylight saving time (DST).

## Decision

1. **Every technician has a time context:** `TimeZoneId` (IANA, e.g. `Europe/Warsaw`) and `CountryCode`
   (ISO 3166-1 alpha-2, e.g. `PL`). Both are required. Windows zone ids and fixed offsets are rejected: an offset
   cannot follow DST changes.
2. **NodaTime with its embedded TZDB** resolves zones and converts times. The zone rules ship in the package, so every
   host (developer machines, CI, containers) uses the same rules regardless of the operating system's tzdata. The
   Workforce module is the only project that references NodaTime. Contracts and the database keep plain types.
3. **Two kinds of time are stored differently:**
   - *Instants* (absences, availability queries): `timestamptz`, normalized to UTC with microsecond precision.
   - *Local rules* (working hours, holidays): `time` and `date` without zone or offset, interpreted in the
     technician's zone for each concrete date.
4. **Local → UTC uses the zone rules for that date.** One resolver is used for every working-hours boundary: a local
   time maps to *the first instant at which the technician's clock shows it*:
   - DST gap (spring forward, the time does not exist): the instant the clock jumps past it. In Warsaw on 2026-03-29,
     02:30 resolves to 03:00 CEST.
   - DST overlap (fall back, the time occurs twice): the earlier occurrence. In Warsaw on 2026-10-25, 02:30 resolves
     to 02:30 CEST (00:30Z), not 02:30 CET (01:30Z).
   With one rule for starts and ends, ranges that touch in local time still touch after conversion. A night shift
   across a DST change keeps its wall-clock hours and changes its real length (7 instead of 8 real hours on spring
   forward, 7 instead of 6 on fall back).
5. **Working hours are half-open `[start, end)` within one day.** An end of `00:00` (written as `24:00` in the API)
   means midnight at the end of the day. A shift crossing midnight is two ranges (Monday 22:00–24:00 and Tuesday
   00:00–06:00). Touching ranges join into one continuous period. Ranges of one technician and day never overlap. The
   database enforces this too, with a `btree_gist` exclusion constraint.
6. **Absences are half-open instant intervals.** They never overlap per technician (also an exclusion constraint) and
   may touch. There is no approval workflow.
7. **Holidays are whole local dates per country.** Uniqueness is `(country_code, date, region_code)` with
   `NULLS NOT DISTINCT`: one national entry per country and day, plus at most one per region. Technicians have no
   region yet, so availability applies national entries only. The calendar is maintained through the API. There is no
   external holiday source.
8. **Availability evaluation** for `[start, end)` runs in this order, and the first failing check gives the reason:
   technician exists (otherwise 404), `IsActive` (`UnavailableInactiveTechnician`), conversion to the technician's
   zone, national holiday on any local date the interval touches (`UnavailableHoliday`), an overlapping absence
   (`UnavailableAbsence`), then working-hours coverage, where the whole interval must lie in one continuous working
   period (`UnavailableOutsideWorkingHours`). Otherwise the result is `Available`. The decision itself
   (`AvailabilityRules`) is pure. The service only loads the rows relevant to the interval. One query may span at
   most 31 days.

## Consequences

- DST handling is explicit and tested on real transition dates, not left to the server's local time.
- Scheduling is not connected yet. When it is, it can consume the availability decision without knowing about zones.
- Regional holidays, per-date working-hour exceptions, and absence approval are deliberately out of scope until the
  roadmap needs them.
- Updating zone rules means updating the NodaTime package (it pins a TZDB release). Ids stored earlier stay valid
  because TZDB keeps old ids as links.
