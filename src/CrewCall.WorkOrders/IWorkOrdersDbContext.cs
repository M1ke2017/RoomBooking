using CrewCall.WorkOrders.Customers;
using CrewCall.WorkOrders.Sites;
using CrewCall.WorkOrders.Visits;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.WorkOrders;

/// <summary>
/// The data the WorkOrders module owns (schema "workorders"). Implemented by CrewCallDbContext in CrewCall.Persistence.
/// The module sees only its own sets, so it cannot query another module's tables (ADR-0004, ADR-0005).
/// </summary>
public interface IWorkOrdersDbContext
{
    DbSet<Customer> Customers { get; }

    DbSet<Site> Sites { get; }

    DbSet<WorkOrder> WorkOrders { get; }

    DbSet<Visit> Visits { get; }

    /// <summary>
    /// Adds an operational event to the current unit of work. It is written by the next <see cref="SaveChangesAsync"/>,
    /// in the same database transaction as the state changes it describes (ADR-0006).
    /// </summary>
    /// <param name="payload">A small record with only what the history needs; serialized to JSON. Never an entity.</param>
    void AppendOperationalEvent(string eventType, string aggregateType, Guid aggregateId, DateTimeOffset occurredAtUtc, object payload);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
