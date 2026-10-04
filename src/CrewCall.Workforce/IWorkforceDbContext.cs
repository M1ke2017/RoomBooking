using CrewCall.Workforce.Technicians;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.Workforce;

/// <summary>
/// The data the Workforce module owns (schema "workforce"). Implemented by CrewCallDbContext in CrewCall.Persistence.
/// The module sees only its own sets, so it cannot query another module's tables (ADR-0004, ADR-0005).
/// </summary>
public interface IWorkforceDbContext
{
    DbSet<Technician> Technicians { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
