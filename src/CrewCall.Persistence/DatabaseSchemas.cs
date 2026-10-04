namespace CrewCall.Persistence;

/// <summary>
/// PostgreSQL schemas and the module that owns each of them.
/// A module writes only to its own schema. Other modules get its data through contracts, not by querying its tables.
/// </summary>
public static class DatabaseSchemas
{
    /// <summary>Owned by WorkOrders: customers, sites, work orders, incidents.</summary>
    public const string WorkOrders = "workorders";

    /// <summary>Owned by Workforce: technicians, teams, skills, working hours, absences.</summary>
    public const string Workforce = "workforce";

    /// <summary>Owned by Resources: vehicles, equipment.</summary>
    public const string Resources = "resources";

    /// <summary>Owned by Scheduling: visits, assignments, the operational calendar.</summary>
    public const string Scheduling = "scheduling";

    /// <summary>Operational and event infrastructure: operational history, outbox, migrations history.</summary>
    public const string Ops = "ops";

    public static IReadOnlyList<string> All { get; } = [WorkOrders, Workforce, Resources, Scheduling, Ops];
}
