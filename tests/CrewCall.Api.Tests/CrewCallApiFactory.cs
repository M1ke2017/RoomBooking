using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Xunit;

[assembly: AssemblyFixture(typeof(CrewCall.Api.Tests.CrewCallApiFactory))]

namespace CrewCall.Api.Tests;

/// <summary>
/// Hosts CrewCall.Api in-process against a real, throwaway PostgreSQL container, so /health exercises the actual
/// database check and the API applies its migrations exactly as it does under Aspire.
/// One instance (one container) is shared by all test classes in this assembly; tests use unique data.
/// </summary>
public sealed class CrewCallApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.3")
        .WithDatabase("crewcall")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        // Start the host (and its startup migration) once, here, before test classes run in parallel.
        // Otherwise their first CreateClient() calls race to start the host and migrate concurrently.
        _ = Services;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("ConnectionStrings:crewcall", _postgres.GetConnectionString());

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
