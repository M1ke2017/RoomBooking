namespace RoomBooking.BookingService.Api

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open RoomBooking.Migrations
open RoomBooking.Infrastructure

module RoomsApi =

    let register (app: WebApplication) : unit =

        app.MapGet(
            "/api/rooms",
            Func<AppDbContext, seq<Room>>(fun db ->
                db.Rooms :> seq<Room>
            )
        )
        |> ignore

        app.MapPost(
            "/api/rooms",
            Func<AppDbContext, Room, IResult>(fun db room ->
                db.Rooms.Add room |> ignore
                db.SaveChanges() |> ignore
                Results.Created($"/api/rooms/{room.Id}", room)
            )
        )
        |> ignore
