namespace RoomBooking.BookingService

open System
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.Configuration  

open RoomBooking.Migrations
open RoomBooking.BookingService.Api

module Program =

    [<EntryPoint>]
    let main args =

        let builder = WebApplication.CreateBuilder(args)

        let connectionString =
            builder.Configuration.GetConnectionString("DefaultConnection")

        builder.Services.AddDbContext<AppDbContext>(fun options ->
            options.UseNpgsql(connectionString) |> ignore
        )
        |> ignore

        builder.Services.AddEndpointsApiExplorer() |> ignore
        builder.Services.AddSwaggerGen() |> ignore

        
        builder.Services.AddHttpClient("IntegrationService", fun client ->
            client.BaseAddress <- Uri("https://localhost:5197")
        )
        |> ignore

        builder.Services.AddCors(fun options ->
            options.AddDefaultPolicy(fun policy ->
                policy
                    .AllowAnyOrigin()
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                |> ignore)
        )
        |> ignore

        let app = builder.Build()

        if app.Environment.IsDevelopment() then
            app.UseSwagger() |> ignore
            app.UseSwaggerUI() |> ignore

        app.UseHttpsRedirection() |> ignore
        app.UseCors() |> ignore

      
        RoomsApi.register app
        UsersApi.register app
        ReservationsApi.register app
        SimulationApi.register app

        app.Run()

        0
