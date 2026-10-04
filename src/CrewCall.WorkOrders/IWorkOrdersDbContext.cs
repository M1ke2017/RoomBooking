using CrewCall.WorkOrders.Customers;
using CrewCall.WorkOrders.Sites;
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

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
