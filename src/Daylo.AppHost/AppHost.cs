var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL in Docker with one database, "daylo". The password is a generated Aspire parameter kept in the AppHost's
// user secrets, never in the repository. The data volume keeps the data between runs.
var database = builder.AddPostgres("daylo-postgres")
    .WithDataVolume()
    .AddDatabase("daylo");

builder.AddProject<Projects.Daylo_Web>("daylo-web")
    .WithReference(database)
    .WaitFor(database)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

builder.Build().Run();
