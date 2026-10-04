module CrewCall.Scheduling.Core.Tests.TimeRangeHelperTests

open System
open Xunit
open CrewCall.Scheduling.Core
open CrewCall.Scheduling.Core.Tests.TestRanges

[<Fact>]
let ``contains includes the boundaries and rejects a range sticking out`` () =
    Assert.True(TimeRange.contains (hours 8 16) (hours 8 16))
    Assert.True(TimeRange.contains (hours 8 16) (hours 10 12))
    Assert.False(TimeRange.contains (hours 8 16) (hours 7 12))
    Assert.False(TimeRange.contains (hours 8 16) (hours 12 17))
    Assert.False(TimeRange.contains (hours 10 12) (hours 8 16))

[<Fact>]
let ``touches is true only for ranges meeting at a boundary`` () =
    Assert.True(TimeRange.touches (hours 8 12) (hours 12 16))
    Assert.True(TimeRange.touches (hours 12 16) (hours 8 12))
    Assert.False(TimeRange.touches (hours 8 12) (hours 13 16))
    Assert.False(TimeRange.touches (hours 8 12) (hours 10 16))

[<Fact>]
let ``touching ranges still do not overlap`` () =
    Assert.False(TimeRange.overlaps (hours 8 12) (hours 12 16))

[<Fact>]
let ``touches compares instants, not local clock values`` () =
    let utcNoon = DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero) // 12:00 at UTC+2
    let morning = range (at 0 8 0) (at 0 12 0)
    let afternoon = range utcNoon (utcNoon.AddHours 4.0)

    Assert.True(TimeRange.touches morning afternoon)

[<Fact>]
let ``merge joins overlapping or touching ranges and refuses a gap`` () =
    Assert.Equal(Some(hours 8 16), TimeRange.merge (hours 8 12) (hours 10 16))
    Assert.Equal(Some(hours 8 16), TimeRange.merge (hours 12 16) (hours 8 12))
    Assert.Equal(Some(hours 8 16), TimeRange.merge (hours 8 16) (hours 10 12))
    Assert.Equal(None, TimeRange.merge (hours 8 12) (hours 13 16))
