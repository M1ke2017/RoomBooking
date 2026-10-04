namespace CrewCall.Scheduling.Reservations;

public enum ResourceType
{
    Technician,
    Vehicle,
    Equipment
}

/// <summary>
/// A technical record that one resource is occupied during [Start, End), optionally for a visit. It is what conflict
/// detection checks against; it is not an assignment. The database rejects two overlapping reservations of the same
/// resource (exclusion constraint); touching reservations are allowed.
/// </summary>
/// <remarks>
/// ResourceId refers to a technician, vehicle or equipment asset owned by another module, and VisitId to a visit; neither
/// has a foreign key, because the referenced tables belong to other modules' schemas.
/// </remarks>
public sealed class ResourceReservation
{
    internal ResourceReservation(Guid id, ResourceType resourceType, Guid resourceId, Guid? visitId, DateTimeOffset start, DateTimeOffset end)
    {
        Id = id;
        ResourceType = resourceType;
        ResourceId = resourceId;
        VisitId = visitId;
        Start = start;
        End = end;
    }

    public Guid Id { get; private set; }

    public ResourceType ResourceType { get; private set; }

    public Guid ResourceId { get; private set; }

    public Guid? VisitId { get; private set; }

    /// <summary>UTC.</summary>
    public DateTimeOffset Start { get; private set; }

    /// <summary>UTC.</summary>
    public DateTimeOffset End { get; private set; }
}
