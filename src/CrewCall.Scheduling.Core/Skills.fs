namespace CrewCall.Scheduling.Core

open System

/// Skill matching by code. Codes are compared after normalization: surrounding whitespace removed, upper case
/// (invariant culture), the same form the Workforce module stores skill codes in. Blank codes carry no requirement.
[<RequireQualifiedAccess>]
module Skills =

    /// The comparable form of a skill code: trimmed, upper case (invariant). "  hvac " becomes "HVAC".
    let normalizeCode (code: string) : string =
        if isNull code then "" else code.Trim().ToUpperInvariant()

    /// The normalized, non-blank codes of a set.
    let normalize (codes: Set<string>) : Set<string> =
        codes |> Set.map normalizeCode |> Set.remove ""

    /// The required codes the candidate does not have, normalized and sorted (ordinal). Empty when all are present.
    let missingSkills (candidateSkills: Set<string>) (requiredSkills: Set<string>) : string list =
        Set.difference (normalize requiredSkills) (normalize candidateSkills)
        |> Set.toList
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

    /// True when the candidate has every required skill. No required skills: always true.
    let hasRequiredSkills (candidateSkills: Set<string>) (requiredSkills: Set<string>) : bool =
        List.isEmpty (missingSkills candidateSkills requiredSkills)
