var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/", () => "CrewCall.Api");

app.Run();
