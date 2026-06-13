namespace RoomBooking.BookingService.Api

open System
open System.Net.Http
open System.Net.Http.Json
open System.Linq

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.EntityFrameworkCore

open RoomBooking.Migrations
open RoomBooking.Infrastructure

module SimulationApi =

    let register (app: WebApplication) : unit =
        app.MapPost(
            "/api/service/simulate",
            Func<AppDbContext, IHttpClientFactory, IResult>(fun db httpFactory ->

                
                let demoUsers =
                    db.Users
                        .Where(fun u -> u.Email.EndsWith("@demo.local"))
                        .ToList()

                if demoUsers.Count = 0 then
                    Results.BadRequest("Brak użytkowników demo (e-maile *@demo.local) w bazie.")
                else

                    
                    let demoUserIds =
                        demoUsers
                        |> Seq.map (fun u -> u.Id)
                        |> Seq.toArray

                    let oldReservations =
                        db.Reservations
                            .Where(fun r -> demoUserIds.Contains(r.UserId))
                            .ToList()

                    if oldReservations.Count > 0 then
                        db.Reservations.RemoveRange(oldReservations) |> ignore
                        db.SaveChanges() |> ignore

                   
                    let rooms = db.Rooms.ToList()

                    if rooms.Count = 0 then
                        Results.BadRequest("Brak sal w bazie – dodaj przynajmniej jedną.")
                    else

                        let rnd = Random()
                        let mutable created = 0
                        let attempts = 40

                        for _ in 1 .. attempts do

                            let user = demoUsers.[rnd.Next demoUsers.Count]
                            let room = rooms.[rnd.Next rooms.Count]

                            let dayOffset = rnd.Next(0, 3)      
                            let hour = rnd.Next(8, 18)          
                            let durationHours = rnd.Next(1, 4)  

                            let startLocal =
                                DateTime
                                    .Today
                                    .AddDays(float dayOffset)
                                    .AddHours(float hour)

                            let endLocal = startLocal.AddHours(float durationHours)

                            let startUtc = startLocal.ToUniversalTime()
                            let endUtc = endLocal.ToUniversalTime()

                            let conflict =
                                db.Reservations.Any(fun r ->
                                    r.RoomId = room.Id
                                    && r.StartUtc < endUtc
                                    && r.EndUtc > startUtc
                                )

                            if not conflict then
                                
                                let client = httpFactory.CreateClient("IntegrationService")

                                if isNull client.BaseAddress then
                                    client.BaseAddress <- Uri("http://localhost:5197")   

                                let dateStr = startUtc.Date.ToString("yyyy-MM-dd")
                                let url = sprintf "api/integration/is-holiday?date=%s" dateStr

                                let response = client.GetAsync(url).Result

                                if response.IsSuccessStatusCode then
                                    let holidayInfo =
                                        response.Content
                                            .ReadFromJsonAsync<{| isHoliday: bool; reason: string option |}>()
                                            .Result

                                    let isHoliday =
                                        not (isNull (box holidayInfo)) && holidayInfo.isHoliday

                                    if not isHoliday then
                                        
                                        let reservation : Reservation =
                                            { Id = 0
                                              RoomId = room.Id
                                              UserId = user.Id
                                              StartUtc = startUtc
                                              EndUtc = endUtc
                                              Status = ReservationStatus.Active
                                              ExternalCalendarEventId = null }

                                        db.Reservations.Add(reservation) |> ignore
                                        created <- created + 1

                        db.SaveChanges() |> ignore

                        Results.Ok(
                            box
                                {| created = created
                                   demoUsers = demoUsers.Count |}
                        )
            )
        )
        |> ignore
