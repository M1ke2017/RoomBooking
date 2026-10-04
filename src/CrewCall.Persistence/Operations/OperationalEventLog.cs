using Microsoft.EntityFrameworkCore;

namespace CrewCall.Persistence.Operations;

/// <summary>Read access to the operational history, for diagnostics. Writes go through the module DbContext interfaces.</summary>
public sealed class OperationalEventLog(CrewCallDbContext db)
{
    /// <summary>Events of one aggregate in the order they happened.</summary>
    public async Task<IReadOnlyList<OperationalEvent>> ListForAggregateAsync(
        string aggregateType, Guid aggregateId, CancellationToken cancellationToken) =>
        await db.OperationalEvents
            .AsNoTracking()
            .Where(e => e.AggregateType == aggregateType && e.AggregateId == aggregateId)
            .OrderBy(e => e.OccurredAtUtc)
            .ThenBy(e => e.Sequence)
            .ToListAsync(cancellationToken);
}
