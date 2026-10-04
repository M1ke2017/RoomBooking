module CrewCall.Scheduling.Core.Tests.ResourceConflictsTests

open System
open Xunit
open CrewCall.Scheduling.Core
open CrewCall.Scheduling.Core.Tests.TestRanges

let private technician = { Kind = Technician; ResourceId = Guid.Parse "00000000-0000-0000-0000-00000000000a" }
let private vehicle = { Kind = Vehicle; ResourceId = Guid.Parse "00000000-0000-0000-0000-00000000000b" }
let private drill = { Kind = Equipment; ResourceId = Guid.Parse "00000000-0000-0000-0000-00000000000c" }
let private tester = { Kind = Equipment; ResourceId = Guid.Parse "00000000-0000-0000-0000-00000000000d" }

let private minutes (m: int) = TimeSpan.FromMinutes(float m)

let private booking (id: int) resource range visitId =
    { BookingId = Guid(id, 0s, 0s, Array.zeroCreate 8)
      Resource = resource
      Range = range
      VisitId = visitId }

let private effective before after visit =
    match TravelBuffer.effectiveRange { Before = minutes before; After = minutes after } visit with
    | Ok range -> range
    | Error error -> failwithf "Expected a valid buffered range, got %A" error

/// Visit A 10:00–11:00 with the given buffers, checked against the bookings, for all four resources.
let private detect before after bookings =
    let visit = hours 10 11
    ResourceConflicts.detect [ technician; vehicle; drill; tester ] visit (effective before after visit) None bookings

// ---------------------------------------------------------------- travel buffer

[<Fact>]
let ``effective range subtracts the before buffer and adds the after buffer`` () =
    let range = effective 15 30 (hours 10 11)

    Assert.Equal(at 0 9 45, range.Start)
    Assert.Equal(at 0 11 30, range.End)

[<Fact>]
let ``a zero buffer keeps the visit range`` () =
    Assert.Equal(hours 10 11, effective 0 0 (hours 10 11))

[<Fact>]
let ``a negative buffer is rejected`` () =
    Assert.Equal(Error NegativeBuffer, TravelBuffer.effectiveRange { Before = minutes -1; After = TimeSpan.Zero } (hours 10 11))
    Assert.Equal(Error NegativeBuffer, TravelBuffer.effectiveRange { Before = TimeSpan.Zero; After = minutes -1 } (hours 10 11))

[<Fact>]
let ``a buffer past the DateTimeOffset range is rejected instead of throwing`` () =
    let lastMinute = range (DateTimeOffset.MaxValue.AddMinutes -10.0) DateTimeOffset.MaxValue
    let firstMinute = range DateTimeOffset.MinValue (DateTimeOffset.MinValue.AddMinutes 10.0)

    Assert.Equal(Error OutOfRange, TravelBuffer.effectiveRange { Before = TimeSpan.Zero; After = minutes 1 } lastMinute)
    Assert.Equal(Error OutOfRange, TravelBuffer.effectiveRange { Before = minutes 1; After = TimeSpan.Zero } firstMinute)

// ---------------------------------------------------------------- conflicts

[<Fact>]
let ``no bookings means no conflicts`` () =
    Assert.Empty(detect 0 0 [])

[<Fact>]
let ``a booking overlapping the visit is a booking overlap`` () =
    let existing = booking 1 vehicle (range (at 0 10 30) (at 0 12 0)) None

    Assert.Equal<ResourceConflict list>([ BookingOverlap existing ], detect 0 0 [ existing ])

[<Fact>]
let ``touching bookings do not conflict without a buffer`` () =
    let before = booking 1 technician (hours 9 10) None
    let after = booking 2 technician (hours 11 12) None

    Assert.Empty(detect 0 0 [ before; after ])

[<Fact>]
let ``a booking inside the after buffer is a travel buffer overlap`` () =
    // Visit A 10:00–11:00 with 30 min after; visit B 11:15–12:00 => conflict.
    let visitB = booking 1 technician (range (at 0 11 15) (at 0 12 0)) None

    Assert.Equal<ResourceConflict list>([ TravelBufferOverlap visitB ], detect 0 30 [ visitB ])

[<Fact>]
let ``a booking touching the end of the after buffer does not conflict`` () =
    // Visit B 11:30–12:00 touches the buffered end 11:30 => no conflict.
    Assert.Empty(detect 0 30 [ booking 1 technician (range (at 0 11 30) (at 0 12 0)) None ])

[<Fact>]
let ``the before buffer applies too and touching its start is allowed`` () =
    let inside = booking 1 vehicle (range (at 0 9 0) (at 0 9 50)) None
    let touching = booking 2 drill (range (at 0 9 0) (at 0 9 45)) None

    Assert.Equal<ResourceConflict list>([ TravelBufferOverlap inside ], detect 15 0 [ inside; touching ])

[<Fact>]
let ``bookings of other resources or other kinds are ignored`` () =
    let otherTechnician = { Kind = Technician; ResourceId = Guid.NewGuid() }
    // Same id as the vehicle but a different kind: a different resource.
    let sameIdOtherKind = { Kind = Equipment; ResourceId = vehicle.ResourceId }

    Assert.Empty(detect 0 0 [ booking 1 otherTechnician (hours 10 11) None; booking 2 sameIdOtherKind (hours 10 11) None ])

[<Fact>]
let ``bookings of the ignored visit are skipped`` () =
    let visitId = Guid.NewGuid()
    let own = booking 1 technician (hours 10 11) (Some visitId)
    let other = booking 2 vehicle (hours 10 11) (Some(Guid.NewGuid()))
    let visit = hours 10 11

    Assert.Equal<ResourceConflict list>(
        [ BookingOverlap other ],
        ResourceConflicts.detect [ technician; vehicle ] visit visit (Some visitId) [ own; other ]
    )

[<Fact>]
let ``every conflicting booking is reported, ordered by resource then start`` () =
    let testerLate = booking 1 tester (range (at 0 10 30) (at 0 11 0)) None
    let testerEarly = booking 2 tester (range (at 0 9 0) (at 0 10 15)) None
    let drillBusy = booking 3 drill (hours 10 11) None
    let technicianBusy = booking 4 technician (range (at 0 10 45) (at 0 12 0)) None

    let expected =
        [ BookingOverlap technicianBusy
          BookingOverlap drillBusy
          BookingOverlap testerEarly
          BookingOverlap testerLate ]

    Assert.Equal<ResourceConflict list>(expected, detect 0 0 [ testerLate; drillBusy; testerEarly; technicianBusy ])
    Assert.Equal<ResourceConflict list>(expected, detect 0 0 [ technicianBusy; testerEarly; drillBusy; testerLate ])
