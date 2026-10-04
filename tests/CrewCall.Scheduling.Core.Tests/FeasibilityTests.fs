module CrewCall.Scheduling.Core.Tests.FeasibilityTests

open System
open Xunit
open CrewCall.Scheduling.Core
open CrewCall.Scheduling.Core.Tests.TestRanges

let private candidateId = Guid.Parse "0199b2a0-0000-7000-8000-000000000001"

let private candidate =
    { CandidateId = candidateId
      IsActive = true
      Availability = [ Available(hours 8 12); Available(hours 12 16) ]
      SkillCodes = set [ "ELECTRICAL"; "HVAC" ] }

let private requirement =
    { RequiredRange = hours 9 15
      RequiredSkillCodes = set [ "electrical" ] }

[<Fact>]
let ``an active candidate with the skills and availability is feasible`` () =
    Assert.Equal(Feasible, Feasibility.evaluateCandidate candidate requirement)

[<Fact>]
let ``an inactive candidate is rejected as inactive`` () =
    Assert.Equal(
        Rejected [ CandidateInactive ],
        Feasibility.evaluateCandidate { candidate with IsActive = false } requirement
    )

[<Fact>]
let ``a missing skill is reported`` () =
    Assert.Equal(
        Rejected [ MissingRequiredSkills [ "FIBER" ] ],
        Feasibility.evaluateCandidate candidate { requirement with RequiredSkillCodes = set [ "Electrical"; "Fiber" ] }
    )

[<Fact>]
let ``a range outside availability is reported`` () =
    Assert.Equal(
        Rejected [ OutsideAvailability [] ],
        Feasibility.evaluateCandidate candidate { requirement with RequiredRange = hours 15 17 }
    )

[<Fact>]
let ``an unavailable window inside available time blocks and gives its reason`` () =
    let absent =
        { candidate with
            Availability = candidate.Availability @ [ Unavailable(hours 10 11, Absence) ] }

    Assert.Equal(Rejected [ OutsideAvailability [ Absence ] ], Feasibility.evaluateCandidate absent requirement)

[<Fact>]
let ``unavailable windows outside the range do not matter`` () =
    let elsewhere =
        { candidate with
            Availability = candidate.Availability @ [ Unavailable(hours 16 18, Holiday); Unavailable(hours 6 9, OutsideWorkingHours) ] }

    Assert.Equal(Feasible, Feasibility.evaluateCandidate elsewhere requirement)

[<Fact>]
let ``inactive and missing skills are both reported`` () =
    Assert.Equal(
        Rejected [ CandidateInactive; MissingRequiredSkills [ "FIBER" ] ],
        Feasibility.evaluateCandidate
            { candidate with IsActive = false }
            { requirement with RequiredSkillCodes = set [ "Fiber" ] }
    )

[<Fact>]
let ``all three failures are reported together, in a fixed order`` () =
    let failing =
        { candidate with
            IsActive = false
            Availability = [ Unavailable(hours 9 12, Holiday); Available(hours 12 13); Unavailable(hours 8 10, Absence) ] }

    let expected =
        Rejected [ CandidateInactive; MissingRequiredSkills [ "FIBER"; "WELDING" ]; OutsideAvailability [ Absence; Holiday ] ]

    Assert.Equal(expected, Feasibility.evaluateCandidate failing { requirement with RequiredSkillCodes = set [ "Welding"; "fiber" ] })

[<Fact>]
let ``reasons do not depend on the order of the availability windows`` () =
    let windows =
        [ Unavailable(hours 9 12, Holiday); Available(hours 12 13); Unavailable(hours 8 10, Absence); Available(hours 6 9) ]

    let evaluate ws =
        Feasibility.evaluateCandidate { candidate with Availability = ws } requirement

    let first = evaluate windows
    Assert.Equal(first, evaluate (List.rev windows))
    Assert.Equal(first, evaluate (List.sortBy string windows))
    Assert.Equal(first, evaluate windows)

[<Fact>]
let ``a candidate with no availability is outside availability`` () =
    Assert.Equal(
        Rejected [ OutsideAvailability [] ],
        Feasibility.evaluateCandidate { candidate with Availability = [] } requirement
    )
