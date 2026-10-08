using System.Collections.Concurrent;
using CrewCall.Integrations;
using CrewCall.Integrations.Messaging;
using CrewCall.Persistence;
using CrewCall.Persistence.Messaging;
using CrewCall.WorkOrders;
using CrewCall.WorkOrders.Executions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

[assembly: AssemblyFixture(typeof(CrewCall.Integrations.Tests.MessagingInfrastructure))]

namespace CrewCall.Integrations.Tests;

/// <summary>
/// The outbox is one shared table and a publisher claims whatever is pending: every test class joins this collection,
/// so tests run one at a time on a clean outbox.
/// </summary>
[CollectionDefinition(Name)]
public sealed class Sequential
{
    public const string Name = "Messaging (sequential)";
}

/// <summary>A migrated PostgreSQL and a RabbitMQ broker, shared by the tests in this assembly.</summary>
public sealed class MessagingInfrastructure : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.3").WithDatabase("crewcall").Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4.1").Build();

    public string DatabaseConnectionString => _postgres.GetConnectionString();

    public string BrokerConnectionString => _rabbitMq.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());
        await using var services = Services(TimeProvider.System);
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().Database.MigrateAsync();
    }

    /// <summary>
    /// Persistence and the WorkOrders module (with every visit assigned) on the shared database, with the given clock: the
    /// write side, as the API composes it. Also the live consumer's read-only routing lookup.
    /// </summary>
    public ServiceProvider Services(TimeProvider clock, string? databaseConnectionString = null) =>
        new ServiceCollection()
            .AddSingleton(clock)
            .AddSingleton<IActiveAssignmentCheck, AlwaysAssigned>()
            .AddCrewCallPersistence(databaseConnectionString ?? DatabaseConnectionString)
            .AddWorkOrdersModule()
            .AddScoped<Live.ILiveRoutingLookup, Live.DbLiveRoutingLookup>()
            .BuildServiceProvider();

    public RabbitMqConnection Broker(string? connectionString = null) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:crewcall-rabbitmq"] = connectionString ?? BrokerConnectionString })
            .Build());

    public static OutboxProcessor Processor(
        ServiceProvider services, IIntegrationMessagePublisher publisher, TimeProvider clock, OutboxPublisherOptions? options = null) =>
        new(services.GetRequiredService<IServiceScopeFactory>(), publisher, Options.Create(options ?? new OutboxPublisherOptions()), clock,
            NullLogger<OutboxProcessor>.Instance);

    /// <summary>Empties the outbox, inbox and receipts (the append-only operational history is left alone).</summary>
    public async Task ResetAsync()
    {
        await using var services = Services(TimeProvider.System);
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().Database.ExecuteSqlRawAsync(
            "TRUNCATE ops.outbox_messages, ops.inbox_messages, ops.integration_event_receipts");
    }

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _rabbitMq.DisposeAsync();
    }

    private sealed class AlwaysAssigned : IActiveAssignmentCheck
    {
        public Task<bool> HasActiveAssignmentAsync(Guid visitId, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}

public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan duration) => _now += duration;
}

/// <summary>Records what would have been published; fails while <see cref="Fail"/> is set.</summary>
public sealed class FakePublisher : IIntegrationMessagePublisher
{
    public ConcurrentQueue<OutboxMessage> Published { get; } = new();

    public Exception? Fail { get; set; }

    public int Calls;

    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        if (Fail is { } failure)
        {
            throw failure;
        }

        Published.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>Keeps formatted log messages, to observe what a background service did.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Enqueue(formatter(state, exception));
}

internal static class Wait
{
    public static async Task UntilAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(100);
        }
    }
}
