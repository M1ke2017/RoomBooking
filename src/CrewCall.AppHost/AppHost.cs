var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL: one physical database, module-owned schemas (see ADR-0004).
// The password is a generated Aspire parameter stored in the AppHost user secrets, never in the repository.
var postgres = builder.AddPostgres("crewcall-postgres")
    .WithDataVolume();

var database = postgres.AddDatabase("crewcall");

var api = builder.AddProject<Projects.CrewCall_Api>("crewcall-api")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.CrewCall_Web>("crewcall-web")
    .WithExternalHttpEndpoints()
    .WithReference(api)
    .WaitFor(api)
    .WithHttpHealthCheck("/health");

builder.Build().Run();
