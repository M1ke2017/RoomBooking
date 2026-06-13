namespace RoomBooking.BookingService.Api

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open RoomBooking.Migrations
open RoomBooking.Infrastructure

module UsersApi =

    let register (app: WebApplication) : unit =

        app.MapGet(
            "/api/users",
            Func<AppDbContext, seq<User>>(fun db ->
                db.Users :> seq<User>
            )
        )
        |> ignore

        app.MapPost(
            "/api/users",
            Func<AppDbContext, User, IResult>(fun db user ->
                db.Users.Add user |> ignore
                db.SaveChanges() |> ignore
                Results.Created($"/api/users/{user.Id}", user)
            )
        )
        |> ignore
