namespace CrewCall.WorkOrders.Incidents;

/// <summary>
/// An urgent request for field work at a customer's site, to be analysed and dispatched by an operator (ADR-0012).
/// Dispatch turns it into a regular work order with a visit; the incident then keeps the link (<see cref="WorkOrderId"/>).
/// The requested window is the half-open interval [RequestedStart, RequestedEnd), stored in UTC.
/// </summary>
public sealed class Incident
{
    public const int TitleMaxLength = WorkOrder.TitleMaxLength;
    public const int DescriptionMaxLength = WorkOrder.DescriptionMaxLength;
    public const int MaxRequiredSkills = 50;
    public const int SkillCodeMaxLength = 50;

    /// <summary>The longest requested window: the longest visit Scheduling can check and claim.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(31);

    private readonly List<IncidentRequiredSkill> _requiredSkills = [];

    internal Incident(
        Guid id,
        Guid customerId,
        Guid siteId,
        string title,
        string? description,
        IncidentPriority priority,
        DateTimeOffset requestedStart,
        DateTimeOffset requestedEnd,
        IEnumerable<string> requiredSkillCodes,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        CustomerId = customerId;
        SiteId = siteId;
        Title = title;
        Description = description;
        Priority = priority;
        Status = IncidentStatus.New;
        RequestedStart = requestedStart;
        RequestedEnd = requestedEnd;
        _requiredSkills.AddRange(requiredSkillCodes.Select(code => new IncidentRequiredSkill(id, code)));
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    // For EF Core materialization.
    private Incident()
    {
        Title = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid CustomerId { get; private set; }

    /// <summary>Always a site of <see cref="CustomerId"/>.</summary>
    public Guid SiteId { get; private set; }

    public string Title { get; private set; }

    public string? Description { get; private set; }

    public IncidentPriority Priority { get; private set; }

    public IncidentStatus Status { get; private set; }

    public DateTimeOffset RequestedStart { get; private set; }

    public DateTimeOffset RequestedEnd { get; private set; }

    /// <summary>Normalized skill codes (trimmed, upper case), each at most once.</summary>
    public IReadOnlyList<IncidentRequiredSkill> RequiredSkills => _requiredSkills;

    /// <summary>The work order created by dispatch; set exactly when the incident is Dispatched or Resolved.</summary>
    public Guid? WorkOrderId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    /// <summary>
    /// Moves to <paramref name="target"/> when the lifecycle allows it; otherwise changes nothing and returns false.
    /// Dispatched and Resolved carry data and have their own methods.
    /// </summary>
    internal bool TryTransitionTo(IncidentStatus target, DateTimeOffset now)
    {
        if (target is IncidentStatus.Dispatched or IncidentStatus.Resolved || !IncidentLifecycle.CanTransition(Status, target))
        {
            return false;
        }

        Status = target;
        UpdatedAtUtc = now;
        return true;
    }

    /// <summary>ReadyForDispatch → Dispatched, linked to the work order created for it. Only ever once.</summary>
    internal bool TryDispatch(Guid workOrderId, DateTimeOffset now)
    {
        if (WorkOrderId is not null || !IncidentLifecycle.CanTransition(Status, IncidentStatus.Dispatched))
        {
            return false;
        }

        WorkOrderId = workOrderId;
        Status = IncidentStatus.Dispatched;
        UpdatedAtUtc = now;
        return true;
    }

    /// <summary>Dispatched → Resolved. The work order and its visits are left as they are.</summary>
    internal bool TryResolve(DateTimeOffset now)
    {
        if (!IncidentLifecycle.CanTransition(Status, IncidentStatus.Resolved))
        {
            return false;
        }

        Status = IncidentStatus.Resolved;
        ResolvedAtUtc = now;
        UpdatedAtUtc = now;
        return true;
    }
}

/// <summary>A skill the incident requires (one row per code, never a list in one column).</summary>
public sealed class IncidentRequiredSkill
{
    internal IncidentRequiredSkill(Guid incidentId, string skillCode)
    {
        IncidentId = incidentId;
        SkillCode = skillCode;
    }

    public Guid IncidentId { get; private set; }

    /// <summary>Trimmed and upper case, like Workforce skill codes.</summary>
    public string SkillCode { get; private set; }
}
