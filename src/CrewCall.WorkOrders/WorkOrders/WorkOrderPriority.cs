namespace CrewCall.WorkOrders;

/// <summary>How urgent a work order is. Urgent is a priority, never a status.</summary>
public enum WorkOrderPriority
{
    Low,
    Normal,
    High,
    Urgent
}
