using CrewCall.Integrations;
using CrewCall.Contracts.Live;
using CrewCall.Integrations.Consumers;
using CrewCall.Integrations.Live;
using CrewCall.Integrations.Messaging;
using CrewCall.Messaging;
using CrewCall.Persistence;
using CrewCall.Persistence.Messaging;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// The database (outbox, inbox) and the broker both come from configuration; under Aspire, from the AppHost.
var connectionString = builder.Configuration.GetConnectionString(PersistenceServiceCollectionExtensions.ConnectionStringName)
    ?? throw new InvalidOperationException(
        $"Connection string '{PersistenceServiceCollectionExtensions.ConnectionStringName}' is not configured. Run CrewCall through CrewCall.AppHost.");

builder.Services.AddCrewCallPersistence(connectionString);
builder.EnrichNpgsqlDbContext<CrewCallDbContext>(settings => settings.DisableHealthChecks = true);

// Readiness of this service: its database and its broker. The API's health does not depend on the broker.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<CrewCallDbContext>("database")
    .AddCheck<RabbitMqHealthCheck>("rabbitmq");

builder.Services.AddOptions<OutboxPublisherOptions>().Bind(builder.Configuration.GetSection("Outbox"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(services => new RabbitMqConnection(services.GetRequiredService<IConfiguration>(), "crewcall-integrations"));
builder.Services.AddSingleton<IIntegrationMessagePublisher, RabbitMqMessagePublisher>();
builder.Services.AddSingleton<OutboxProcessor>();
builder.Services.AddHostedService<OutboxPublisherWorker>();
builder.Services.AddSingleton<IntegrationAuditHandler>();
builder.Services.AddHostedService<IntegrationAuditConsumer>();

// Live operations (ADR-0015): RabbitMQ → live consumer → SignalR. The hub is a delivery channel, never a source of truth.
builder.Services.AddSignalR();
builder.Services.AddOptions<LiveOperationsOptions>().Bind(builder.Configuration.GetSection("LiveOperations"));
builder.Services.AddScoped<LiveRoutingLookup>();
builder.Services.AddSingleton<ILiveOperationsPublisher, SignalRLiveOperationsPublisher>();
builder.Services.AddSingleton<LiveOperationsHandler>();
builder.Services.AddHostedService<LiveOperationsConsumer>();

// RabbitMQ.Client's own publish/receive activities, and SignalR's built-in hub activities, in the Aspire traces.
builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing
    .AddSource("RabbitMQ.Client.*")
    .AddSource("Microsoft.AspNetCore.SignalR.Server"));

var app = builder.Build();

app.MapGet("/", () => "CrewCall.Integrations");

// Available whenever this process runs; it does not depend on the broker being up (no messages arrive while it is down).
app.MapHub<LiveOperationsHub>(LiveOperationsHubContract.Path);

if (app.Environment.IsDevelopment())
{
    // Development only: how much is waiting and how much was dead-lettered. No outbox CRUD.
    app.MapGet("/outbox/status", async (OutboxStore outbox, CancellationToken cancellationToken) =>
    {
        var (pending, failed) = await outbox.CountsAsync(cancellationToken);
        return Results.Ok(new { pending, failed });
    });
}

app.MapDefaultEndpoints();

app.Run();
