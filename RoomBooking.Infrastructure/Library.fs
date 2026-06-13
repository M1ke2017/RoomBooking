namespace RoomBooking.Infrastructure

open System
open Microsoft.FSharp.Core

type ReservationStatus =
    | Active = 0
    | Cancelled = 1
    | Finished = 2

[<CLIMutable>]
type Room =
    { Id: int
      Name: string
      Capacity: int }

[<CLIMutable>]
type User =
    { Id: int
      Name: string
      Email: string }

[<CLIMutable>]
type Reservation =
    { Id: int
      RoomId: int
      UserId: int
      StartUtc: DateTime
      EndUtc: DateTime
      Status: ReservationStatus
      ExternalCalendarEventId: string }

[<CLIMutable>]
type ExternalCalendarEventDto =
    { ExternalId: string option
      Title: string
      StartUtc: DateTime
      EndUtc: DateTime
      Location: string }
