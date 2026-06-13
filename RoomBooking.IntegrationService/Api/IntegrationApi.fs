namespace RoomBooking.IntegrationService.Api

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open RoomBooking.Infrastructure

module IntegrationApi =

    type HolidayCheckResult =
        { isHoliday: bool
          reason: string option }

    let register (app: WebApplication) : unit =

        app.MapPost(
            "/api/integration/calendar-events",
            Func<ExternalCalendarEventDto, IResult>(fun dto ->
                let enriched =
                    { dto with ExternalId = Some(Guid.NewGuid().ToString()) }

                Results.Ok(enriched)
            )
        )
        |> ignore

        app.MapGet(
            "/api/integration/is-holiday",
            Func<DateTime, IResult>(fun date ->

                let isWeekend =
                    date.DayOfWeek = DayOfWeek.Saturday
                    || date.DayOfWeek = DayOfWeek.Sunday

                let result =
                    if isWeekend then
                        { isHoliday = true
                          reason = Some "Weekend" }
                    else
                        { isHoliday = false
                          reason = None }

                Results.Ok(result)
            )
        )
        |> ignore
