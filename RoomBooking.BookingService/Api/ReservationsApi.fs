namespace RoomBooking.BookingService.Api

open System
open System.Net.Http
open System.Net.Http.Json
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.EntityFrameworkCore
open System.Linq

open RoomBooking.Migrations
open RoomBooking.Infrastructure

module ReservationsApi =

    let register (app: WebApplication) : unit =

        app.MapGet(
            "/api/reservations",
            Func<AppDbContext, seq<Reservation>>(fun db ->
                db.Reservations :> seq<Reservation>
            )
        )
        |> ignore

        app.MapPost(
        "/api/reservations",
        Func<AppDbContext, Reservation, IHttpClientFactory, IResult>(fun db reservation httpFactory ->

            let roomExists =
                db.Rooms.Any(fun r -> r.Id = reservation.RoomId)

            if not roomExists then
                Results.BadRequest("Room does not exist")
            else

                let conflict =
                    db.Reservations.Any(fun r ->
                        r.RoomId = reservation.RoomId
                        && r.Id <> reservation.Id
                        && r.StartUtc < reservation.EndUtc
                        && r.EndUtc > reservation.StartUtc
                    )

                if conflict then
                    Results.Conflict("Time slot already booked")
                else

                    
                    let client = httpFactory.CreateClient("IntegrationService")

                    if isNull client.BaseAddress then
                        client.BaseAddress <- Uri("http://localhost:5197")   

                    let date = reservation.StartUtc.Date
                    let dateStr = date.ToString("yyyy-MM-dd")
                    let url = sprintf "api/integration/is-holiday?date=%s" dateStr

                    let response = client.GetAsync(url).Result


                    if not response.IsSuccessStatusCode then
                        Results.StatusCode(StatusCodes.Status502BadGateway)
                    else
                        let holidayInfo =
                            response.Content.ReadFromJsonAsync<{| isHoliday: bool; reason: string option |}>().Result

                        let isHoliday =
                            not (isNull (box holidayInfo)) && holidayInfo.isHoliday

                        if isHoliday then
                            Results.BadRequest("Cannot book room on holiday")
                        else
                            db.Reservations.Add reservation |> ignore
                            db.SaveChanges() |> ignore
                            Results.Created($"/api/reservations/{reservation.Id}", reservation)
                )
        )
        |> ignore
