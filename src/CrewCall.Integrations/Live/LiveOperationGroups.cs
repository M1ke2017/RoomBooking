using CrewCall.Contracts.Integration;

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
    /// The groups an event's live message goes to: <see cref="All"/>, the incident's group, the technicians' groups (named
    /// by the event or looked up) and the site's group. Each group appears once.
    /// </summary>
    public static IReadOnlyList<string> For(IIntegrationEvent integrationEvent, LiveRoutingContext routing)
    {
        var (incidentId, technicianId) = integrationEvent switch
        {
            AssignmentCreatedIntegrationEvent created => ((Guid?)null, (Guid?)created.TechnicianId),
            IncidentDispatchedIntegrationEvent dispatched => (dispatched.IncidentId, dispatched.TechnicianId),
            VisitRescheduledIntegrationEvent rescheduled => (rescheduled.IncidentId, null),
            _ => (null, null)
        };

        var groups = new List<string> { All };
        if (incidentId is { } incident && incident != Guid.Empty)
        {
            groups.Add(Incident(incident));
        }

        foreach (var technician in routing.TechnicianIds.Prepend(technicianId ?? Guid.Empty).Where(id => id != Guid.Empty))
        {
            groups.Add(Technician(technician));
        }

        if (routing.SiteId is { } site && site != Guid.Empty)
        {
            groups.Add(Site(site));
        }

        return groups.Distinct().ToList();
    }

    // "D" format: lower-case, hyphenated, so one id always gives one group name.
    private static string Build(string prefix, Guid id) =>
        id == Guid.Empty
            ? throw new ArgumentException($"A {prefix} id is required; the empty GUID is not a valid id.", nameof(id))
            : $"{prefix}:{id:D}";
}
