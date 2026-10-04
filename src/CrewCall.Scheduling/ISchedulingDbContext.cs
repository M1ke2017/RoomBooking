using CrewCall.Scheduling.Reservations;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.Scheduling;

/// <summary>
/// The data the Scheduling module owns (schema "scheduling"). Implemented by CrewCallDbContext in CrewCall.Persistence.
/// </summary>
public interface ISchedulingDbContext
{
    DbSet<ResourceReservation> ResourceReservations { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
