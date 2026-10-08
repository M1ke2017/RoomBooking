var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL: one physical database, module-owned schemas (see ADR-0004).
// The password is a generated Aspire parameter stored in the AppHost user secrets, never in the repository.
var postgres = builder.AddPostgres("crewcall-postgres")
    .WithDataVolume();

var database = postgres.AddDatabase("crewcall");

// The reporting service's own database (ADR-0016): a second logical database on the same server, with its own
// connection string and migrations. Nothing joins across the two.
var reportingDatabase = postgres.AddDatabase("crewcall-reporting", "crewcall_reporting");

// RabbitMQ: integration events from the transactional outbox (ADR-0014). Credentials are generated Aspire parameters;
// the management UI is for development only and nothing depends on it.
var rabbitMq = builder.AddRabbitMQ("crewcall-rabbitmq")
    .WithManagementPlugin();

var api = builder.AddProject<Projects.CrewCall_Api>("crewcall-api")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/health");

// Publishes the outbox, runs the integration-audit and live-operations consumers, and hosts the live operations SignalR
// hub (ADR-0014, ADR-0015). Waits for the API, which applies the migrations.
var integrations = builder.AddProject<Projects.CrewCall_Integrations>("crewcall-integrations")
    .WithReference(database)
    .WithReference(rabbitMq)
    .WaitFor(api)
    .WaitFor(rabbitMq)
    .WithHttpHealthCheck("/health");

// Event-driven reporting projections and the read-only Reporting API (ADR-0016). It references only its own database
// and the broker: never the operational database, never the API.
builder.AddProject<Projects.CrewCall_Reporting>("crewcall-reporting-service")
    .WithReference(reportingDatabase)
    .WithReference(rabbitMq)
    .WaitFor(reportingDatabase)
    .WaitFor(rabbitMq)
    .WithHttpHealthCheck("/health");

// The Web finds the API and the live operations hub through service discovery. It waits only for the API: without the
// broker or the Integrations service it still runs, it just receives no live updates.
builder.AddProject<Projects.CrewCall_Web>("crewcall-web")
    .WithExternalHttpEndpoints()
    .WithReference(api)
    .WithReference(integrations)
    .WaitFor(api)
    .WithHttpHealthCheck("/health");

builder.Build().Run();
