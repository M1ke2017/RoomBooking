open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting

open RoomBooking.IntegrationService.Api

let builder = WebApplication.CreateBuilder()

builder.Services.AddEndpointsApiExplorer() |> ignore
builder.Services.AddSwaggerGen() |> ignore

let app = builder.Build()

if app.Environment.IsDevelopment() then
    app.UseSwagger()   |> ignore
    app.UseSwaggerUI() |> ignore

app.UseHttpsRedirection() |> ignore

IntegrationApi.register app

app.Run()
