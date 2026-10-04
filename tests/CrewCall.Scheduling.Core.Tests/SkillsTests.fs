module CrewCall.Scheduling.Core.Tests.SkillsTests

open Xunit
open CrewCall.Scheduling.Core

let private candidate = set [ "Electrical"; "Diagnostics"; "HVAC" ]

[<Fact>]
let ``no required skills always match`` () =
    Assert.True(Skills.hasRequiredSkills candidate Set.empty)
    Assert.True(Skills.hasRequiredSkills Set.empty Set.empty)

[<Fact>]
let ``exactly the required skills match`` () =
    Assert.True(Skills.hasRequiredSkills (set [ "Electrical"; "HVAC" ]) (set [ "Electrical"; "HVAC" ]))

[<Fact>]
let ``a candidate with more skills than required matches`` () =
    Assert.True(Skills.hasRequiredSkills candidate (set [ "Electrical"; "HVAC" ]))

[<Fact>]
let ``one missing skill fails and is reported`` () =
    Assert.False(Skills.hasRequiredSkills candidate (set [ "Electrical"; "Fiber" ]))
    Assert.Equal<string list>([ "FIBER" ], Skills.missingSkills candidate (set [ "Electrical"; "Fiber" ]))

[<Fact>]
let ``every missing skill is reported, normalized and sorted`` () =
    Assert.Equal<string list>(
        [ "FIBER"; "PLUMBING"; "WELDING" ],
        Skills.missingSkills candidate (set [ "welding"; "Electrical"; "Plumbing"; "fiber" ])
    )

[<Fact>]
let ``codes compare trimmed and case-insensitively`` () =
    Assert.True(Skills.hasRequiredSkills (set [ " electrical "; "hvac" ]) (set [ "ELECTRICAL"; "Hvac  " ]))
    Assert.Equal("HVAC", Skills.normalizeCode "  hvac ")

[<Fact>]
let ``blank required codes carry no requirement`` () =
    Assert.True(Skills.hasRequiredSkills Set.empty (set [ ""; "   " ]))
