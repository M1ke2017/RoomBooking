using CrewCall.Messaging;
using CrewCall.Reporting;
using CrewCall.Reporting.Api;
using CrewCall.Reporting.Consumers;
using CrewCall.Reporting.Data;
using CrewCall.Reporting.Projections;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// The reporting database only (ADR-0016). This service has no connection string for the operational database.
var connectionString = builder.Configuration.GetConnectionString(ReportingDbContext.ConnectionStringName)
    ?? throw new InvalidOperationException(
        $"Connection string '{ReportingDbContext.ConnectionStringName}' is not configured. Run CrewCall through CrewCall.AppHost.");

builder.Services.AddDbContext<ReportingDbContext>(options => ReportingDbContext.Configure(options, connectionString));
builder.EnrichNpgsqlDbContext<ReportingDbContext>(settings => settings.DisableHealthChecks = true);

// Readiness: the reporting database and the broker. Liveness (/alive) is the process only.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ReportingDbContext>("database")
    .AddCheck<RabbitMqHealthCheck>("rabbitmq");

builder.Services.AddOptions<ReportingOptions>().Bind(builder.Configuration.GetSection("Reporting"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(services => new RabbitMqConnection(services.GetRequiredService<IConfiguration>(), "crewcall-reporting"));
builder.Services.AddScoped<IReportingProjectionProcessor, ReportingProjectionProcessor>();
builder.Services.AddScoped<IReportingProjectionResetService, ReportingProjectionResetService>();
builder.Services.AddScoped<ReportingQueries>();
builder.Services.AddHostedService<ReportingConsumer>();

builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource("RabbitMQ.Client.*"));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // Development only: this service applies its own migrations. A dedicated migration step replaces this before any
    // shared environment.
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<ReportingDbContext>().Database.MigrateAsync();
}

app.MapGet("/", () => "CrewCall.Reporting");
app.MapReportingEndpoints();
app.MapDefaultEndpoints();

app.Run();
