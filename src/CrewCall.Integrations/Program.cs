using CrewCall.Integrations;
using CrewCall.Integrations.Consumers;
using CrewCall.Integrations.Messaging;
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
builder.Services.AddSingleton<RabbitMqConnection>();
builder.Services.AddSingleton<IIntegrationMessagePublisher, RabbitMqMessagePublisher>();
builder.Services.AddSingleton<OutboxProcessor>();
builder.Services.AddHostedService<OutboxPublisherWorker>();
builder.Services.AddSingleton<IntegrationAuditHandler>();
builder.Services.AddHostedService<IntegrationAuditConsumer>();

// RabbitMQ.Client's own publish/receive activities in the Aspire traces.
builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource("RabbitMQ.Client.*"));

var app = builder.Build();

app.MapGet("/", () => "CrewCall.Integrations");

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
