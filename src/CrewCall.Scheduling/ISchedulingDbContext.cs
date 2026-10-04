using CrewCall.Scheduling.Assignments;
using CrewCall.Scheduling.Reservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CrewCall.Scheduling;

/// <summary>
/// The data the Scheduling module owns (schema "scheduling"). Implemented by CrewCallDbContext in CrewCall.Persistence.
/// </summary>
public interface ISchedulingDbContext
{
    DbSet<ResourceReservation> ResourceReservations { get; }

    DbSet<Assignment> Assignments { get; }

    DbSet<AssignmentEquipment> AssignmentEquipment { get; }

    /// <summary>For explicit transactions and the execution strategy around an atomic resource claim.</summary>
    DatabaseFacade Database { get; }

    ChangeTracker ChangeTracker { get; }

    /// <summary>
    /// Adds an operational event to the current unit of work, written by the next SaveChanges in the same transaction
    /// as the state change it describes (ADR-0006).
    /// </summary>
    void AppendOperationalEvent(string eventType, string aggregateType, Guid aggregateId, DateTimeOffset occurredAtUtc, object payload);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
