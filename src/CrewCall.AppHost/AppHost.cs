var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL: one physical database, module-owned schemas (see ADR-0004).
// The password is a generated Aspire parameter stored in the AppHost user secrets, never in the repository.
var postgres = builder.AddPostgres("crewcall-postgres")
    .WithDataVolume();

var database = postgres.AddDatabase("crewcall");

// RabbitMQ: integration events from the transactional outbox (ADR-0014). Credentials are generated Aspire parameters;
// the management UI is for development only and nothing depends on it.
var rabbitMq = builder.AddRabbitMQ("crewcall-rabbitmq")
    .WithManagementPlugin();

var api = builder.AddProject<Projects.CrewCall_Api>("crewcall-api")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/health");

// Publishes the outbox and runs the integration-audit consumer. Waits for the API, which applies the migrations.
builder.AddProject<Projects.CrewCall_Integrations>("crewcall-integrations")
    .WithReference(database)
    .WithReference(rabbitMq)
    .WaitFor(api)
    .WaitFor(rabbitMq)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.CrewCall_Web>("crewcall-web")
    .WithExternalHttpEndpoints()
    .WithReference(api)
    .WaitFor(api)
    .WithHttpHealthCheck("/health");

builder.Build().Run();
