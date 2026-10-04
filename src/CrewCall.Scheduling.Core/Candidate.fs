namespace CrewCall.Scheduling.Core

open System

/// Someone who could be scheduled, reduced to what the feasibility rules need. Built by adapters from module data
/// (for example a Workforce technician); it is not a Technician entity and carries no identity beyond CandidateId.
type SchedulingCandidate =
    { CandidateId: Guid
      IsActive: bool
      /// Available and unavailable windows, in any order. Touching or overlapping available windows join.
      Availability: AvailabilityWindow list
      /// Skill codes; compared after normalization (see Skills.normalizeCode).
      SkillCodes: Set<string> }

/// What a piece of work needs from a candidate.
type SchedulingRequirement =
    { RequiredRange: TimeRange
      /// Every code must be present on the candidate. Empty: no skill requirement.
      RequiredSkillCodes: Set<string> }
