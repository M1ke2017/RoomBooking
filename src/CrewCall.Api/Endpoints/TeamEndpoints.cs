using CrewCall.Contracts.Teams;
using CrewCall.Workforce.Teams;
using CrewCall.Workforce.Technicians;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

internal static class TeamEndpoints
{
    public static IEndpointRouteBuilder MapTeamEndpoints(this IEndpointRouteBuilder app)
    {
        var teams = app.MapGroup("/api/teams").WithTags("Teams");
        teams.MapPost("/", CreateAsync).WithName("CreateTeam");
        teams.MapGet("/", ListAsync).WithName("ListTeams");
        teams.MapPost("/{teamId:guid}/technicians/{technicianId:guid}", AddMemberAsync).WithName("AddTeamMember");
        teams.MapDelete("/{teamId:guid}/technicians/{technicianId:guid}", RemoveMemberAsync).WithName("RemoveTeamMember");
        teams.MapGet("/{teamId:guid}/technicians", ListMembersAsync).WithName("ListTeamMembers");

        return app;
    }

    private static async Task<Results<Created<TeamResponse>, ValidationProblem>> CreateAsync(
        CreateTeamRequest request, TeamService teams, CancellationToken cancellationToken)
    {
        var outcome = await teams.CreateAsync(new CreateTeam(request.Name, request.IsActive), cancellationToken);

        return outcome switch
        {
            CreateTeamOutcome.Created created => TypedResults.Created((string?)null, created.Team.ToResponse()),
            CreateTeamOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static async Task<Ok<TeamResponse[]>> ListAsync(TeamService teams, CancellationToken cancellationToken)
    {
        var list = await teams.ListAsync(cancellationToken);
        return TypedResults.Ok(list.Select(team => team.ToResponse()).ToArray());
    }

    /// <summary>200 when added or already a member; 409 when the technician is in another team (never moved silently).</summary>
    private static async Task<Results<Ok<TeamTechnicianResponse>, ProblemHttpResult>> AddMemberAsync(
        Guid teamId, Guid technicianId, TeamService teams, CancellationToken cancellationToken)
    {
        var outcome = await teams.AddMemberAsync(teamId, technicianId, cancellationToken);

        return outcome switch
        {
            AddTeamMemberOutcome.Added added => TypedResults.Ok(added.Technician.ToTeamTechnicianResponse(teamId)),
            AddTeamMemberOutcome.AlreadyMember member => TypedResults.Ok(member.Technician.ToTeamTechnicianResponse(teamId)),
            AddTeamMemberOutcome.MemberOfAnotherTeam other => ApiProblems.Conflict(
                "Technician already belongs to another team",
                $"Technician '{other.TechnicianId}' belongs to team '{other.CurrentTeamId}'. Remove them from that team first."),
            AddTeamMemberOutcome.TeamNotFound notFound => ApiProblems.TeamNotFound(notFound.TeamId),
            AddTeamMemberOutcome.TechnicianNotFound notFound => ApiProblems.TechnicianNotFound(notFound.TechnicianId),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    /// <summary>Idempotent: 204 also when the technician was not in this team.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveMemberAsync(
        Guid teamId, Guid technicianId, TeamService teams, CancellationToken cancellationToken)
    {
        var outcome = await teams.RemoveMemberAsync(teamId, technicianId, cancellationToken);

        return outcome switch
        {
            RemoveTeamMemberOutcome.Removed => TypedResults.NoContent(),
            RemoveTeamMemberOutcome.TeamNotFound notFound => ApiProblems.TeamNotFound(notFound.TeamId),
            RemoveTeamMemberOutcome.TechnicianNotFound notFound => ApiProblems.TechnicianNotFound(notFound.TechnicianId),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static async Task<Results<Ok<TeamTechnicianResponse[]>, ProblemHttpResult>> ListMembersAsync(
        Guid teamId, TeamService teams, CancellationToken cancellationToken)
    {
        var list = await teams.ListMembersAsync(teamId, cancellationToken);

        return list is null
            ? ApiProblems.TeamNotFound(teamId)
            : TypedResults.Ok(list.Select(technician => technician.ToTeamTechnicianResponse(teamId)).ToArray());
    }

    internal static TeamResponse ToResponse(this Team team) => new(team.Id, team.Name, team.IsActive);

    private static TeamTechnicianResponse ToTeamTechnicianResponse(this Technician technician, Guid teamId) =>
        new(teamId, technician.Id, technician.DisplayName, technician.Email, technician.IsActive);
}
