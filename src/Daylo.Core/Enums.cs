namespace Daylo.Core;

public enum Priority
{
    Low,
    Normal,
    High,
    Urgent
}

/// <summary>Named WorkTaskStatus, not TaskStatus, so it never collides with System.Threading.Tasks.TaskStatus.</summary>
public enum WorkTaskStatus
{
    Planned,
    InProgress,
    Completed,
    Cancelled
}

public enum IncidentStatus
{
    New,
    Analyzed,
    Assigned,
    Resolved,
    Cancelled
}

public enum AssignmentStatus
{
    Active,
    Cancelled
}

public enum ResourceType
{
    Vehicle,
    Equipment
}
