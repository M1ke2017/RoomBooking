using System.Collections.Concurrent;
using CrewCall.Persistence;
using CrewCall.WorkOrders.Executions;
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

        _services = Services(TimeProvider.System);

        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().Database.MigrateAsync();
    }

    /// <summary>Which visits have an active assignment (Scheduling's answer, faked): by default every visit.</summary>
    public FakeActiveAssignments Assignments { get; } = new();

    /// <summary>The module on the same database, but with its own clock (dispose it after the test).</summary>
    public ServiceProvider CreateProvider(TimeProvider clock) => Services(clock);

    private ServiceProvider Services(TimeProvider clock) =>
        new ServiceCollection()
            .AddSingleton(clock)
            .AddSingleton<IActiveAssignmentCheck>(Assignments)
            .AddCrewCallPersistence(_postgres.GetConnectionString())
            .AddWorkOrdersModule()
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

/// <summary>Stands in for Scheduling's assignments: every visit is assigned unless marked unassigned.</summary>
public sealed class FakeActiveAssignments : IActiveAssignmentCheck
{
    private readonly ConcurrentDictionary<Guid, bool> _unassigned = new();

    public void MarkUnassigned(Guid visitId) => _unassigned[visitId] = true;

    public Task<bool> HasActiveAssignmentAsync(Guid visitId, CancellationToken cancellationToken) =>
        Task.FromResult(!_unassigned.ContainsKey(visitId));
}
