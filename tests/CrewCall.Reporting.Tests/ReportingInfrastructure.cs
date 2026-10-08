extern alias reporting;

using System.Text.Json;
using CrewCall.Contracts.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using reporting::CrewCall.Reporting;
using reporting::CrewCall.Reporting.Api;
using reporting::CrewCall.Reporting.Data;
using reporting::CrewCall.Reporting.Projections;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

[assembly: AssemblyFixture(typeof(CrewCall.Reporting.Tests.ReportingInfrastructure))]

namespace CrewCall.Reporting.Tests;

/// <summary>The reporting tables are shared: every class that writes them joins this collection, one test at a time.</summary>
[CollectionDefinition(Name)]
public sealed class Sequential
{
    public const string Name = "Reporting (sequential)";
}

/// <summary>
/// One PostgreSQL server with the two logical databases the AppHost creates (crewcall and crewcall_reporting) and one
/// RabbitMQ broker, shared by the tests in this assembly.
/// </summary>
public sealed class ReportingInfrastructure : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.3").WithDatabase("crewcall").Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4.1").Build();

    public string OperationalConnectionString => _postgres.GetConnectionString();

    /// <summary>The reporting service's own database on the same server: a different connection string.</summary>
    public string ReportingConnectionString => new Npgsql.NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = "crewcall_reporting" }.ConnectionString;

    public string BrokerConnectionString => _rabbitMq.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());
        await using var services = Services(TimeProvider.System);
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ReportingDbContext>().Database.MigrateAsync();
    }

    /// <summary>The projection engine, the reset service and the queries over the reporting database, with the given clock.</summary>
    public ServiceProvider Services(TimeProvider clock, ReportingOptions? options = null) =>
        new ServiceCollection()
            .AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .AddSingleton(clock)
            .AddSingleton(Options.Create(options ?? new ReportingOptions()))
            .AddDbContext<ReportingDbContext>(builder => ReportingDbContext.Configure(builder, ReportingConnectionString))
            .AddScoped<IReportingProjectionProcessor, ReportingProjectionProcessor>()
            .AddScoped<IReportingProjectionResetService, ReportingProjectionResetService>()
            .AddScoped<ReportingQueries>()
            .BuildServiceProvider();

    public async Task ResetAsync()
    {
        await using var services = Services(TimeProvider.System);
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IReportingProjectionResetService>().ResetAsync(includeInbox: true, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _rabbitMq.DisposeAsync();
    }
}

public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan duration) => _now += duration;
}

/// <summary>Integration events and their envelopes, as the outbox publisher would send them (MessageId = EventId).</summary>
internal static class Events
{
    public static readonly DateTimeOffset Day1 = new(2038, 6, 7, 0, 0, 0, TimeSpan.Zero);

    public static IntegrationEventEnvelope Envelope(IIntegrationEvent integrationEvent)
    {
        var descriptor = IntegrationEventCatalog.Describe(integrationEvent);
        using var payload = JsonDocument.Parse(IntegrationEventCatalog.SerializePayload(integrationEvent));
        return new IntegrationEventEnvelope(
            integrationEvent.EventId, descriptor.Type, descriptor.Version, integrationEvent.OccurredAtUtc, integrationEvent.CorrelationId,
            payload.RootElement.Clone());
    }

    public static VisitCreatedIntegrationEvent VisitCreated(
        Guid visitId, DateTimeOffset plannedStart, TimeSpan plannedLength, Guid? siteId = null, DateTimeOffset? at = null) =>
        new(Guid.NewGuid(), at ?? plannedStart.AddDays(-1), null, visitId, Guid.NewGuid(), Guid.NewGuid(), siteId ?? Guid.NewGuid(),
            plannedStart, plannedStart + plannedLength, at ?? plannedStart.AddDays(-1), "Normal");

    public static AssignmentCreatedIntegrationEvent AssignmentCreated(Guid assignmentId, Guid visitId, Guid technicianId, DateTimeOffset at) =>
        new(Guid.NewGuid(), at, null, assignmentId, visitId, technicianId, null, []);

    public static AssignmentReplacedIntegrationEvent AssignmentReplaced(Guid oldAssignmentId, Guid newAssignmentId, Guid visitId, DateTimeOffset at) =>
        new(Guid.NewGuid(), at, null, oldAssignmentId, newAssignmentId, visitId);

    public static AssignmentCancelledIntegrationEvent AssignmentCancelled(Guid assignmentId, Guid visitId, DateTimeOffset at) =>
        new(Guid.NewGuid(), at, null, assignmentId, visitId);

    public static VisitWorkCompletedIntegrationEvent WorkCompleted(
        Guid visitId, DateTimeOffset at, decimal? travel, decimal gross, decimal pause) =>
        new(Guid.NewGuid(), at, null, visitId, Guid.NewGuid(), travel, gross, pause, gross - pause);

    public static VisitStatusChangedIntegrationEvent StatusChanged(Guid visitId, string from, string to, DateTimeOffset at) =>
        new(Guid.NewGuid(), at, null, visitId, from, to);

    public static IncidentDispatchedIntegrationEvent IncidentDispatched(
        Guid incidentId, Guid visitId, Guid assignmentId, Guid technicianId, DateTimeOffset at) =>
        new(Guid.NewGuid(), at, null, incidentId, Guid.NewGuid(), visitId, assignmentId, technicianId);
}
