using CrewCall.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

[assembly: AssemblyFixture(typeof(CrewCall.WorkOrders.Tests.WorkOrdersDatabase))]

namespace CrewCall.WorkOrders.Tests;

/// <summary>A migrated PostgreSQL shared by the tests in this assembly, with the module wired as in production.</summary>
public sealed class WorkOrdersDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.3").Build();
    private ServiceProvider? _services;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        _services = new ServiceCollection()
            .AddCrewCallPersistence(_postgres.GetConnectionString())
            .AddWorkOrdersModule()
            .BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().Database.MigrateAsync();
    }

    /// <summary>A new scope, like one HTTP request: services in it share one DbContext.</summary>
    public AsyncServiceScope CreateScope() =>
        (_services ?? throw new InvalidOperationException("Database not initialized.")).CreateAsyncScope();

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }
}
