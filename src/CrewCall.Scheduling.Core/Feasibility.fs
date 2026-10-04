namespace CrewCall.Scheduling.Core

/// Why a candidate cannot take a requirement.
type RejectionReason =
    | CandidateInactive
    /// The required skill codes the candidate lacks, normalized and sorted.
    | MissingRequiredSkills of string list
    /// The required range is not inside one continuous available period. Carries the reasons of the unavailable
    /// windows overlapping it (distinct, in declaration order); empty when the input gave no reason, e.g. the range
    /// simply falls outside every available window.
    | OutsideAvailability of AvailabilityReason list

/// The outcome of checking one candidate against one requirement.
type FeasibilityResult =
    | Feasible
    /// Every applicable reason, never empty, always in the order CandidateInactive, MissingRequiredSkills,
    /// OutsideAvailability.
    | Rejected of RejectionReason list

[<RequireQualifiedAccess>]
module Feasibility =

    /// Checks activity, skills and availability, and reports every failing check (it does not stop at the first).
    /// Pure and deterministic: no clock, randomness, I/O or exceptions; the same input always gives the same result,
    /// whatever the order of the candidate's availability windows.
    let evaluateCandidate (candidate: SchedulingCandidate) (requirement: SchedulingRequirement) : FeasibilityResult =
        let inactive =
            if candidate.IsActive then [] else [ CandidateInactive ]

        let skills =
            match Skills.missingSkills candidate.SkillCodes requirement.RequiredSkillCodes with
            | [] -> []
            | missing -> [ MissingRequiredSkills missing ]

        let availability =
            if Availability.covers requirement.RequiredRange candidate.Availability then
                []
            else
                [ OutsideAvailability(Availability.unavailableReasonsWithin requirement.RequiredRange candidate.Availability) ]

        match inactive @ skills @ availability with
        | [] -> Feasible
        | reasons -> Rejected reasons
