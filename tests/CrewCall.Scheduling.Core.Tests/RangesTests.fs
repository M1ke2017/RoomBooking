module CrewCall.Scheduling.Core.Tests.RangesTests

open Xunit
open CrewCall.Scheduling.Core
open CrewCall.Scheduling.Core.Tests.TestRanges

// ---------------------------------------------------------------- normalizeRanges

[<Fact>]
let ``normalize of no ranges is empty`` () =
    Assert.Empty(Ranges.normalizeRanges [])

[<Fact>]
let ``normalize of one range is that range`` () =
    Assert.Equal<TimeRange list>([ hours 8 12 ], Ranges.normalizeRanges [ hours 8 12 ])

[<Fact>]
let ``sorted separate ranges stay unchanged`` () =
    let ranges = [ hours 8 10; hours 11 12; hours 14 16 ]

    Assert.Equal<TimeRange list>(ranges, Ranges.normalizeRanges ranges)

[<Fact>]
let ``unsorted ranges come back sorted`` () =
    Assert.Equal<TimeRange list>(
        [ hours 8 10; hours 11 12; hours 14 16 ],
        Ranges.normalizeRanges [ hours 14 16; hours 8 10; hours 11 12 ]
    )

[<Fact>]
let ``overlapping ranges merge`` () =
    Assert.Equal<TimeRange list>([ hours 8 14 ], Ranges.normalizeRanges [ hours 8 12; hours 10 14 ])

[<Fact>]
let ``a range inside another is absorbed`` () =
    Assert.Equal<TimeRange list>([ hours 8 16 ], Ranges.normalizeRanges [ hours 8 16; hours 10 12 ])

[<Fact>]
let ``touching ranges merge`` () =
    Assert.Equal<TimeRange list>([ hours 8 16 ], Ranges.normalizeRanges [ hours 8 12; hours 12 16 ])

[<Fact>]
let ``a chain of ranges merges into one, whatever the input order`` () =
    // The example from the sprint: 10–12, 08–10, 11–14 => 08–14.
    Assert.Equal<TimeRange list>([ hours 8 14 ], Ranges.normalizeRanges [ hours 10 12; hours 8 10; hours 11 14 ])
    Assert.Equal<TimeRange list>([ hours 6 18 ], Ranges.normalizeRanges [ hours 14 18; hours 6 8; hours 10 14; hours 8 10 ])

[<Fact>]
let ``a gap keeps ranges separate`` () =
    Assert.Equal<TimeRange list>(
        [ hours 8 12; hours 13 16 ],
        Ranges.normalizeRanges [ hours 13 16; hours 8 12 ]
    )

[<Fact>]
let ``normalize leaves its input unchanged`` () =
    let input = [| hours 14 16; hours 8 10; hours 9 12 |]
    let copy = Array.copy input

    Ranges.normalizeRanges input |> ignore

    Assert.Equal<TimeRange[]>(copy, input)
    Assert.Equal<(System.DateTimeOffset * System.DateTimeOffset) list>(bounds (List.ofArray copy), bounds (List.ofArray input))

// ---------------------------------------------------------------- isRangeFullyCovered

[<Fact>]
let ``an exactly matching window covers the range`` () =
    Assert.True(Ranges.isRangeFullyCovered (hours 8 16) [ hours 8 16 ])

[<Fact>]
let ``a smaller requested range is covered`` () =
    Assert.True(Ranges.isRangeFullyCovered (hours 10 12) [ hours 8 16 ])

[<Fact>]
let ``a range starting before the window is not covered`` () =
    Assert.False(Ranges.isRangeFullyCovered (hours 7 12) [ hours 8 16 ])

[<Fact>]
let ``a range ending after the window is not covered`` () =
    Assert.False(Ranges.isRangeFullyCovered (hours 12 17) [ hours 8 16 ])

[<Fact>]
let ``touching windows cover a range together`` () =
    Assert.True(Ranges.isRangeFullyCovered (hours 10 15) [ hours 8 12; hours 12 16 ])

[<Fact>]
let ``windows with a gap cannot cover a range together`` () =
    Assert.False(Ranges.isRangeFullyCovered (hours 10 15) [ hours 8 12; hours 13 16 ])

[<Fact>]
let ``unsorted windows still cover`` () =
    Assert.True(Ranges.isRangeFullyCovered (hours 9 17) [ hours 16 18; hours 12 16; hours 8 12 ])

[<Fact>]
let ``overlapping windows normalize into one covering period`` () =
    Assert.True(Ranges.isRangeFullyCovered (hours 9 17) [ hours 8 14; hours 13 18; hours 10 11 ])

[<Fact>]
let ``a range crossing midnight is covered by windows on both days`` () =
    let lateShift = range (at 0 22 0) (at 1 0 0)
    let earlyShift = range (at 1 0 0) (at 1 6 0)

    Assert.True(Ranges.isRangeFullyCovered (range (at 0 23 0) (at 1 2 0)) [ earlyShift; lateShift ])
    Assert.False(Ranges.isRangeFullyCovered (range (at 0 23 0) (at 1 2 0)) [ lateShift ])

[<Fact>]
let ``no windows cover nothing`` () =
    Assert.False(Ranges.isRangeFullyCovered (hours 8 9) [])
