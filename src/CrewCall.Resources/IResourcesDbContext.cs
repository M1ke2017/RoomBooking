using Microsoft.EntityFrameworkCore;

namespace CrewCall.Resources;

/// <summary>
/// The data the Resources module owns (schema "resources"). Implemented by CrewCallDbContext in CrewCall.Persistence.
/// The module sees only its own sets, so it cannot query another module's tables (ADR-0004, ADR-0005).
/// </summary>
public interface IResourcesDbContext
{
    DbSet<Vehicles.Vehicle> Vehicles { get; }

    DbSet<Equipment.EquipmentItem> Equipment { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
