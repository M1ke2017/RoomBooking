using CrewCall.Api.Endpoints;
using CrewCall.Api.SchedulingAdapters;
using CrewCall.Persistence;
using CrewCall.Resources;
using CrewCall.Scheduling;
using CrewCall.WorkOrders;
using CrewCall.Workforce;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// The connection string comes from configuration. When run through the AppHost, Aspire supplies it.
var connectionString = builder.Configuration.GetConnectionString(PersistenceServiceCollectionExtensions.ConnectionStringName)
    ?? throw new InvalidOperationException(
        $"Connection string '{PersistenceServiceCollectionExtensions.ConnectionStringName}' is not configured. Run CrewCall through CrewCall.AppHost.");

builder.Services.AddCrewCallPersistence(connectionString);

// Aspire enrichment: retries, OpenTelemetry. The health check is registered explicitly so it has a stable name.
builder.EnrichNpgsqlDbContext<CrewCallDbContext>(settings => settings.DisableHealthChecks = true);
builder.Services.AddHealthChecks()
    .AddDbContextCheck<CrewCallDbContext>("database");

builder.Services.AddWorkOrdersModule();
builder.Services.AddWorkforceModule();
builder.Services.AddResourcesModule();
builder.Services.AddSchedulingModule();
builder.Services.AddSchedulingAdapters();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    // Development only: apply migrations on startup. A dedicated migration step replaces this before any shared environment.
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().Database.MigrateAsync();
}

app.MapGet("/", () => "CrewCall.Api");

app.MapCustomerEndpoints();
app.MapSiteEndpoints();
app.MapTechnicianEndpoints();
app.MapSkillEndpoints();
app.MapTeamEndpoints();
app.MapWorkingHoursEndpoints();
app.MapAbsenceEndpoints();
app.MapHolidayEndpoints();
app.MapAvailabilityEndpoints();
app.MapVehicleEndpoints();
app.MapEquipmentEndpoints();
app.MapWorkOrderEndpoints();
app.MapVisitEndpoints();
app.MapOperationsEndpoints();
app.MapSchedulingEndpoints();
app.MapAssignmentEndpoints();
app.MapOperationalCalendarEndpoints();

app.MapDefaultEndpoints();

app.Run();
