extern alias reporting;

using System.Text.Json;
using CrewCall.Contracts.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using reporting::CrewCall.Reporting.Api;
using reporting::CrewCall.Reporting.Data;
using reporting::CrewCall.Reporting.Projections;
using Xunit;

namespace CrewCall.Reporting.Tests;

/// <summary>Drives the projection engine directly (no broker) and reads the reporting database back.</summary>
internal sealed class ProjectionHarness(ReportingInfrastructure infrastructure, TimeProvider clock) : IAsyncDisposable
{
    private readonly ServiceProvider _services = infrastructure.Services(clock);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public Task<ProjectionResult> ProcessAsync(IIntegrationEvent integrationEvent) => ProcessAsync(Events.Envelope(integrationEvent));

    public async Task<ProjectionResult> ProcessAsync(IntegrationEventEnvelope envelope)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IReportingProjectionProcessor>().ProcessAsync(envelope, Cancellation);
    }

    public async Task ProcessAllAsync(IEnumerable<IntegrationEventEnvelope> envelopes)
    {
        foreach (var envelope in envelopes)
        {
            await ProcessAsync(envelope);
        }
    }

    public async Task ResetAsync(bool includeInbox = true)
    {
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IReportingProjectionResetService>().ResetAsync(includeInbox, Cancellation);
    }

    public async Task<T> QueryAsync<T>(Func<ReportingQueries, Task<T>> query)
    {
        await using var scope = _services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<ReportingQueries>());
    }

    public async Task<T> ReadAsync<T>(Func<ReportingDbContext, Task<T>> read)
    {
        await using var scope = _services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<ReportingDbContext>());
    }

    public Task<List<TechnicianActivityDaily>> DaysAsync(Guid technicianId) =>
        ReadAsync(db => db.TechnicianActivityDays.AsNoTracking().Where(d => d.TechnicianId == technicianId).OrderBy(d => d.DateUtc).ToListAsync(Cancellation));

    public Task<VisitActivity?> VisitAsync(Guid visitId) =>
        ReadAsync(db => db.VisitActivities.AsNoTracking().SingleOrDefaultAsync(v => v.VisitId == visitId, Cancellation));

    public Task<List<AssignmentActivity>> AssignmentsAsync(Guid visitId) =>
        ReadAsync(db => db.AssignmentActivities.AsNoTracking().Where(a => a.VisitId == visitId).OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.AssignmentId).ToListAsync(Cancellation));

    public Task<int> InboxCountAsync(Guid messageId) =>
        ReadAsync(db => db.InboxMessages.CountAsync(m => m.MessageId == messageId, Cancellation));

    /// <summary>Every reporting row, in key order, as JSON: two read models are the same exactly when their snapshots are.</summary>
    public Task<string> SnapshotAsync() => ReadAsync(async db => JsonSerializer.Serialize(new
    {
        Days = await db.TechnicianActivityDays.AsNoTracking().OrderBy(d => d.TechnicianId).ThenBy(d => d.DateUtc).ToListAsync(Cancellation),
        Visits = await db.VisitActivities.AsNoTracking().OrderBy(v => v.VisitId).ToListAsync(Cancellation),
        Assignments = await db.AssignmentActivities.AsNoTracking().OrderBy(a => a.AssignmentId).ToListAsync(Cancellation),
        Incidents = await db.IncidentActivities.AsNoTracking().OrderBy(i => i.IncidentId).ToListAsync(Cancellation),
        Checkpoints = await db.ProjectionCheckpoints.AsNoTracking().OrderBy(c => c.ProjectionName).ToListAsync(Cancellation),
        Inbox = await db.InboxMessages.AsNoTracking().OrderBy(m => m.ConsumerName).ThenBy(m => m.MessageId).ToListAsync(Cancellation)
    }));

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}
