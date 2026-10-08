using CrewCall.Contracts.Live;

namespace CrewCall.Integrations.Live;

/// <summary>
/// The SignalR group names (ADR-0015). They are always built here, on the server, from a typed id: a client never sends
/// a group name, so it cannot join an arbitrary group.
/// </summary>
public static class LiveOperationGroups
{
    /// <summary>Every live message.</summary>
    public const string All = "all";

    public static string Site(Guid siteId) => Build("site", siteId);

    public static string Technician(Guid technicianId) => Build("technician", technicianId);

    public static string Incident(Guid incidentId) => Build("incident", incidentId);

    /// <summary>
    /// The groups a message goes to: <see cref="All"/>, plus the site, technician and incident groups of the entity it is
    /// about and of its related entities. Each group appears once.
    /// </summary>
    public static IReadOnlyList<string> For(LiveOperationMessage message)
    {
        var groups = new List<string> { All };
        foreach (var entity in message.Related.Prepend(new LiveEntityReference(message.EntityType, message.EntityId)))
        {
            var group = entity.EntityType switch
            {
                LiveEntityTypes.Site => Site(entity.EntityId),
                LiveEntityTypes.Technician => Technician(entity.EntityId),
                LiveEntityTypes.Incident => Incident(entity.EntityId),
                _ => null
            };

            if (group is not null && !groups.Contains(group))
            {
                groups.Add(group);
            }
        }

        return groups;
    }

    // "D" format: lower-case, hyphenated, so one id always gives one group name.
    private static string Build(string prefix, Guid id) =>
        id == Guid.Empty
            ? throw new ArgumentException($"A {prefix} id is required; the empty GUID is not a valid id.", nameof(id))
            : $"{prefix}:{id:D}";
}
