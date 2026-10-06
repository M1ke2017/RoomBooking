# ADR-0011: Resource matching and scoring

- **Status:** Accepted
- **Date:** 2026-10-06
- **Sprint:** 9

## Context

The scheduling check (ADR-0008) answers "can *this* technician take the visit?". Dispatchers also need the next
question answered: "of everyone who could, who fits best?". The answer must be explainable, because people act on it,
and reproducible, because two dispatchers asking the same question must get the same answer.

## Decision

1. **Matching is advisory.**
   - `POST /api/scheduling/match` (`ResourceMatchingService`) writes nothing. It reserves no resource and never
     creates an assignment.
   - Nothing picks the top candidate automatically.
   - Assigning remains a separate commit (ADR-0009) with its own final check and atomic claim, so a ranked candidate
     can still be refused if the situation changed in between.
2. **Feasibility is the gate, not a score.**
   - A candidate is *Rejected*, with every reason and without a score, when either of these holds:
     - the 6A feasibility decision rejects them: inactive, a missing required skill, or not available according to
       Workforce;
     - a requested resource clashes with an existing booking: their own technician reservation, or the requested
       vehicle or equipment.
   - Only *Eligible* candidates are scored and ranked.
   - Vehicle and equipment can make a request infeasible for everyone, but they never change a technician's score.
3. **Scoring is pure, explicit and bounded.**
   - `Scheduling.Core.Matching` (F#) computes `CandidateScore { TotalScore; Components }` on a 0–100 scale. There are
     four components, each its normalized weight × a 0–1 factor:

     | Component | Default weight | Factor |
     |---|---|---|
     | Skills | 35 | share of required skills held; 1 for an eligible candidate; extra skills add nothing |
     | Availability | 30 | 1, because eligibility already requires full availability (there is no "partially available") |
     | Workload | 25 | `1 − assigned minutes / 480`, floored at 0: less work scores higher |
     | Team preference | 10 | 1 in the preferred team or with no preference (neutral, everyone equal); 0 outside it |

   - The weights live in one `ScoringPolicy` value (`ScoringWeights` plus the full-workload horizon), not as magic
     numbers. They are normalized, so any non-negative weights work. They are code defaults; there is no database
     configuration yet.
   - Components are rounded **down** to two decimals and `TotalScore` is their exact sum. A response always explains
     its total and never exceeds 100.
4. **Workload comes from the current relational state.**
   - The C# read side counts each technician's *active* assignments: minutes and count, from their technician
     reservations, clipped to the workload window.
   - The window defaults to the UTC days the request touches and can be set explicitly.
   - Claimed windows include travel buffers, and travel counts as work.
   - Replaced and cancelled assignments hold no reservations, so they never count. Operational events are not used.
5. **The ranking is deterministic.** The order is:
   1. eligible before rejected;
   2. `TotalScore` descending;
   3. fewer assigned minutes;
   4. fewer assignments;
   5. candidate id.

   Rejected candidates are ordered by id. The same input gives the same output, regardless of input order
   (property-tested).
6. **Not AI or ML.** The score is a transparent, hand-weighted formula with no learning and no historical or
   statistical model. It can be reasoned about, tested exactly and explained to the person who acts on it.
7. **Bounded work.**
   - Candidates are the requested subset or, by default, all active technicians, capped at 200. The response reports
     truncation.
   - Up to 10 eligible candidates are returned by default (at most 50), and up to 50 rejected ones. Totals are always
     reported.
   - Conflicts and workload are each loaded with one query for all candidates. The Workforce availability decision is
     taken per technician, as Workforce exposes it.

## Consequences

- Dispatchers see *why* someone ranks first: every component, the workload and the skill coverage. They also see why
  others were rejected, using the same reason codes as the scheduling check.
- Tuning the weights is a one-place change and can later move to configuration without touching the formula.
- With hundreds of technicians the per-technician availability calls dominate the cost. A batch availability API in
  Workforce is the next step if that becomes a problem.
