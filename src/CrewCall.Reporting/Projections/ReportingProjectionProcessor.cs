using System.Diagnostics;
using CrewCall.Contracts.Integration;
using CrewCall.Reporting.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CrewCall.Reporting.Projections;

public enum ProjectionResult
{
    /// <summary>Projected and recorded in the reporting inbox, in one transaction.</summary>
    Applied,

    /// <summary>This consumer projected the message before: nothing changed.</summary>
    Duplicate,

    /// <summary>A type or version the projections do not use: ignored on purpose, nothing recorded.</summary>
    Unsupported
}

/// <summary>
/// Applies integration events to the reporting projections (ADR-0016). Independent of the transport: the RabbitMQ
/// consumer and a replay of stored envelopes both go through it.
/// </summary>
public interface IReportingProjectionProcessor
{
    Task<ProjectionResult> ProcessAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// One message, one transaction in the reporting database:
/// inbox record (consumer, MessageId) → projection handler → daily recompute → checkpoint → commit.
/// The inbox insert uses ON CONFLICT DO NOTHING: a message seen before changes nothing. Anything failing rolls back the
/// whole message, inbox record included, so a redelivery projects it again from the start.
/// Projection writes are serialized by a transaction-scoped advisory lock, so two consumers (or a consumer and a
/// replay) never recompute the same technician day from different snapshots.
/// </summary>
public sealed class ReportingProjectionProcessor(
    ReportingDbContext db, TimeProvider clock, IOptions<ReportingOptions> options, ILogger<ReportingProjectionProcessor> logger)
    : IReportingProjectionProcessor
{
    public const string ProjectionName = "crewcall-reporting";

    // Arbitrary, stable key of the advisory lock that serializes projection transactions.
    private const long ProjectionLockKey = 0x43_52_45_57_52_45_50;

    private static readonly AssignmentCreatedProjectionHandler AssignmentCreated = new();
    private static readonly AssignmentReplacedProjectionHandler AssignmentReplaced = new();
    private static readonly AssignmentCancelledProjectionHandler AssignmentCancelled = new();
    private static readonly IncidentDispatchedProjectionHandler IncidentDispatched = new();
    private static readonly VisitCreatedProjectionHandler VisitCreated = new();
    private static readonly VisitStatusChangedProjectionHandler VisitStatusChanged = new();
    private static readonly VisitWorkCompletedProjectionHandler VisitWorkCompleted = new();

    public async Task<ProjectionResult> ProcessAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (ReportingEventReader.Read(envelope) is not { } integrationEvent)
        {
            logger.LogWarning("Reporting ignored {Type} v{Version} {MessageId}: not projected.", envelope.Type, envelope.Version, envelope.MessageId);
            return ProjectionResult.Unsupported;
        }

        var handler = HandlerName(integrationEvent);
        var consumerName = options.Value.ConsumerName;
        var now = clock.GetUtcNow();

        ProjectionResult result;
        try
        {
            result = await db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({ProjectionLockKey})", token);

                var recorded = await db.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO reporting.inbox_messages (consumer_name, message_id, type, version, received_at_utc, processed_at_utc)
                    VALUES ({consumerName}, {envelope.MessageId}, {envelope.Type}, {envelope.Version}, {now}, {now})
                    ON CONFLICT (consumer_name, message_id) DO NOTHING
                    """,
                    token);
                if (recorded == 0)
                {
                    await transaction.RollbackAsync(token);
                    return ProjectionResult.Duplicate;
                }

                var context = new ProjectionContext(db, now);
                await ApplyAsync(integrationEvent, context, token);
                await context.SaveAsync(token);
                await DailyActivityRecalculator.RecalculateAsync(db, context.AffectedDays(), now, token);
                await AdvanceCheckpointAsync(envelope.OccurredAtUtc, now, token);
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return ProjectionResult.Applied;
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Projection {Handler} failed for {Type} {MessageId}; nothing was recorded.", handler, envelope.Type, envelope.MessageId);
            throw;
        }

        if (result == ProjectionResult.Duplicate)
        {
            logger.LogInformation("Duplicate {Type} {MessageId} skipped by reporting.", envelope.Type, envelope.MessageId);
            return result;
        }

        // How far behind the business the reporting read side is, for this message.
        var lagMilliseconds = Math.Max(0, (clock.GetUtcNow() - envelope.OccurredAtUtc).TotalMilliseconds);
        Activity.Current?.SetTag("crewcall.reporting.lag_ms", lagMilliseconds);
        logger.LogInformation(
            "Projection {Handler} applied {Type} v{Version} {MessageId}; ReportingLagMilliseconds {ReportingLagMilliseconds:F0}.",
            handler, envelope.Type, envelope.Version, envelope.MessageId, lagMilliseconds);
        return result;
    }

    private static Task ApplyAsync(IIntegrationEvent integrationEvent, ProjectionContext context, CancellationToken cancellationToken) =>
        integrationEvent switch
        {
            AssignmentCreatedIntegrationEvent created => AssignmentCreated.ApplyAsync(created, context, cancellationToken),
            AssignmentReplacedIntegrationEvent replaced => AssignmentReplaced.ApplyAsync(replaced, context, cancellationToken),
            AssignmentCancelledIntegrationEvent cancelled => AssignmentCancelled.ApplyAsync(cancelled, context, cancellationToken),
            IncidentDispatchedIntegrationEvent dispatched => IncidentDispatched.ApplyAsync(dispatched, context, cancellationToken),
            VisitCreatedIntegrationEvent visitCreated => VisitCreated.ApplyAsync(visitCreated, context, cancellationToken),
            VisitStatusChangedIntegrationEvent statusChanged => VisitStatusChanged.ApplyAsync(statusChanged, context, cancellationToken),
            VisitWorkCompletedIntegrationEvent completed => VisitWorkCompleted.ApplyAsync(completed, context, cancellationToken),
            _ => throw new ArgumentException($"{integrationEvent.GetType().Name} has no projection handler.", nameof(integrationEvent))
        };

    private static string HandlerName(IIntegrationEvent integrationEvent) => integrationEvent switch
    {
        AssignmentCreatedIntegrationEvent => AssignmentCreated.Name,
        AssignmentReplacedIntegrationEvent => AssignmentReplaced.Name,
        AssignmentCancelledIntegrationEvent => AssignmentCancelled.Name,
        IncidentDispatchedIntegrationEvent => IncidentDispatched.Name,
        VisitCreatedIntegrationEvent => VisitCreated.Name,
        VisitStatusChangedIntegrationEvent => VisitStatusChanged.Name,
        VisitWorkCompletedIntegrationEvent => VisitWorkCompleted.Name,
        _ => integrationEvent.GetType().Name
    };

    private async Task AdvanceCheckpointAsync(DateTimeOffset occurredAtUtc, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var checkpoint = await db.ProjectionCheckpoints.FindAsync([ProjectionName], cancellationToken);
        if (checkpoint is null)
        {
            checkpoint = new ProjectionCheckpoint { ProjectionName = ProjectionName };
            db.ProjectionCheckpoints.Add(checkpoint);
        }

        var occurred = occurredAtUtc.ToUniversalTime();
        if (checkpoint.LastEventOccurredAtUtc is null || occurred > checkpoint.LastEventOccurredAtUtc)
        {
            checkpoint.LastEventOccurredAtUtc = occurred;
        }

        checkpoint.LastProcessedAtUtc = now;
        checkpoint.ProcessedMessages++;
    }
}
