using CrewCall.Workforce.Technicians;

namespace CrewCall.Workforce.Teams;

/// <param name="IsActive">Defaults to true when not provided.</param>
public sealed record CreateTeam(string? Name, bool? IsActive);

public abstract record CreateTeamOutcome
{
    private CreateTeamOutcome()
    {
    }

    public sealed record Created(Team Team) : CreateTeamOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CreateTeamOutcome;
}

/// <summary>
/// Adding a technician who is already in this team reports AlreadyMember and changes nothing.
/// A technician in another team is never moved silently: that is MemberOfAnotherTeam.
/// </summary>
public abstract record AddTeamMemberOutcome
{
    private AddTeamMemberOutcome()
    {
    }

    public sealed record Added(Technician Technician) : AddTeamMemberOutcome;

    public sealed record AlreadyMember(Technician Technician) : AddTeamMemberOutcome;

    public sealed record MemberOfAnotherTeam(Guid TechnicianId, Guid CurrentTeamId) : AddTeamMemberOutcome;

    public sealed record TeamNotFound(Guid TeamId) : AddTeamMemberOutcome;

    public sealed record TechnicianNotFound(Guid TechnicianId) : AddTeamMemberOutcome;
}

/// <summary>Removing a technician who is not in this team also reports Removed (idempotent).</summary>
public abstract record RemoveTeamMemberOutcome
{
    private RemoveTeamMemberOutcome()
    {
    }

    public sealed record Removed : RemoveTeamMemberOutcome;

    public sealed record TeamNotFound(Guid TeamId) : RemoveTeamMemberOutcome;

    public sealed record TechnicianNotFound(Guid TechnicianId) : RemoveTeamMemberOutcome;
}
