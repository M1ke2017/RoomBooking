using CrewCall.Persistence;
using CrewCall.Scheduling.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

[assembly: AssemblyFixture(typeof(CrewCall.Scheduling.Tests.SchedulingDatabase))]

namespace CrewCall.Scheduling.Tests;

/// <summary>
/// A migrated PostgreSQL shared by the tests in this assembly, with the Scheduling module wired as in production except
/// for its ports: <see cref="Technicians"/>, <see cref="Resources"/> and <see cref="Visits"/> stand in for Workforce,
/// Resources and WorkOrders.
/// </summary>
public sealed class SchedulingDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.3").Build();
    private ServiceProvider? _services;

    public FakeTechnicianSource Technicians { get; } = new();

    public FakeResourceCatalog Resources { get; } = new();

    public FakeVisitSource Visits { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        _services = new ServiceCollection()
            .AddCrewCallPersistence(_postgres.GetConnectionString())
            .AddSchedulingModule()
            .AddSingleton<ITechnicianSchedulingSource>(Technicians)
            .AddSingleton<IResourceCatalog>(Resources)
            .AddSingleton<IVisitSchedulingSource>(Visits)
            .BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().Database.MigrateAsync();
    }

    /// <summary>
    /// A service provider over the same database whose ports are <paramref name="technicians"/> (and the shared resources and visits),
    /// for tests that must see only their own technicians, such as candidate discovery.
    /// </summary>
    public ServiceProvider CreateIsolatedProvider(FakeTechnicianSource technicians) =>
        new ServiceCollection()
            .AddCrewCallPersistence(_postgres.GetConnectionString())
            .AddSchedulingModule()
            .AddSingleton<ITechnicianSchedulingSource>(technicians)
            .AddSingleton<IResourceCatalog>(Resources)
            .AddSingleton<IVisitSchedulingSource>(Visits)
            .BuildServiceProvider();

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
