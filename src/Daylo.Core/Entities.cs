namespace Daylo.Core;

/// <summary>A person in the field team.</summary>
public sealed class Worker
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? PhoneNumber { get; set; }
    public bool IsActive { get; set; } = true;
    public List<Skill> Skills { get; set; } = [];
}

/// <summary>What a worker can do, e.g. ELECTRICAL.</summary>
public sealed class Skill
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Code { get; set; }
    public required string Name { get; set; }
}

public sealed class Customer
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
}

/// <summary>A place where work happens, belonging to a customer.</summary>
public sealed class Site
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public required string Name { get; set; }
    public required string Address { get; set; }
}

/// <summary>
/// A planned piece of work. Named WorkTask, not Task, so it never collides with System.Threading.Tasks.Task; the table
/// and the screens call it a task (zadanie).
/// </summary>
public sealed class WorkTask
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;
    public required string Title { get; set; }
    public string? Description { get; set; }
    public Priority Priority { get; set; } = Priority.Normal;
    public WorkTaskStatus Status { get; set; } = WorkTaskStatus.Planned;
    public DateTimeOffset PlannedStart { get; set; }
    public DateTimeOffset PlannedEnd { get; set; }
    public List<Assignment> Assignments { get; set; } = [];

    /// <summary>Concurrency token (PostgreSQL xmin): two decisions about the same task cannot both win.</summary>
    public uint Version { get; set; }
}

/// <summary>Urgent work reported by a customer, waiting for someone to take it.</summary>
public sealed class Incident
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;
    public required string Title { get; set; }
    public Priority Priority { get; set; } = Priority.Urgent;
    public Guid? RequiredSkillId { get; set; }
    public Skill? RequiredSkill { get; set; }
    public DateTimeOffset RequestedStart { get; set; }
    public DateTimeOffset RequestedEnd { get; set; }
    public IncidentStatus Status { get; set; } = IncidentStatus.New;

    /// <summary>Concurrency token (PostgreSQL xmin): an incident is accepted once.</summary>
    public uint Version { get; set; }
}

/// <summary>Who does a task.</summary>
public sealed class Assignment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TaskId { get; set; }
    public WorkTask Task { get; set; } = null!;
    public Guid WorkerId { get; set; }
    public Worker Worker { get; set; } = null!;
    public AssignmentStatus Status { get; set; } = AssignmentStatus.Active;
}

/// <summary>A vehicle or a piece of equipment.</summary>
public sealed class Resource
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public ResourceType Type { get; set; }
    public bool IsAvailable { get; set; } = true;
}

/// <summary>One line of the decision history, e.g. "Task X moved from 10:00 to 14:00 due to urgent incident."</summary>
public sealed class ActivityLog
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Type { get; set; }
    public Guid? EntityId { get; set; }
    public required string Message { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
