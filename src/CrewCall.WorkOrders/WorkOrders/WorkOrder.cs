namespace CrewCall.WorkOrders;

/// <summary>A unit of work requested by a customer at one of its sites. Carried out through one or more visits.</summary>
public sealed class WorkOrder
{
    public const int TitleMaxLength = 200;
    public const int DescriptionMaxLength = 4000;

    internal WorkOrder(
        Guid id,
        Guid customerId,
        Guid siteId,
        string title,
        string? description,
        WorkOrderPriority priority,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        CustomerId = customerId;
        SiteId = siteId;
        Title = title;
        Description = description;
        Priority = priority;
        Status = WorkOrderStatus.Open;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid CustomerId { get; private set; }

    /// <summary>The site where the work is done. Always a site of <see cref="CustomerId"/>.</summary>
    public Guid SiteId { get; private set; }

    public string Title { get; private set; }

    public string? Description { get; private set; }

    public WorkOrderPriority Priority { get; private set; }

    public WorkOrderStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Moves to <paramref name="target"/> when the lifecycle allows it; otherwise changes nothing and returns false.</summary>
    internal bool TryTransitionTo(WorkOrderStatus target)
    {
        if (!WorkOrderLifecycle.CanTransition(Status, target))
        {
            return false;
        }

        Status = target;
        return true;
    }
}
