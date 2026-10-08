using CrewCall.Reporting.Data;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.Reporting.Projections;

/// <summary>
/// The rebuild foundation (ADR-0016): empties the projections so they can be built again by replaying integration
/// event envelopes through <see cref="IReportingProjectionProcessor"/>. A development and test operation; it is not
/// exposed over HTTP. The schema and its migrations are kept.
/// </summary>
public interface IReportingProjectionResetService
{
    /// <summary>
    /// Truncates every projection table and the checkpoint. With <paramref name="includeInbox"/>, also the reporting
    /// inbox: needed before replaying the same messages, which the inbox would otherwise skip as duplicates.
    /// </summary>
    Task ResetAsync(bool includeInbox, CancellationToken cancellationToken);
}

public sealed class ReportingProjectionResetService(ReportingDbContext db, ILogger<ReportingProjectionResetService> logger)
    : IReportingProjectionResetService
{
    public async Task ResetAsync(bool includeInbox, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            includeInbox
                ? "TRUNCATE reporting.technician_activity, reporting.visit_activity, reporting.assignment_activity, reporting.incident_activity, reporting.projection_checkpoints, reporting.inbox_messages"
                : "TRUNCATE reporting.technician_activity, reporting.visit_activity, reporting.assignment_activity, reporting.incident_activity, reporting.projection_checkpoints",
            cancellationToken);
        logger.LogWarning("Reporting projections reset (inbox included: {IncludeInbox}).", includeInbox);
    }
}
