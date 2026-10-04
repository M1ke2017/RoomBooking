using CrewCall.Contracts.Skills;
using CrewCall.Workforce.Skills;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

internal static class SkillEndpoints
{
    public static IEndpointRouteBuilder MapSkillEndpoints(this IEndpointRouteBuilder app)
    {
        var skills = app.MapGroup("/api/skills").WithTags("Skills");
        skills.MapPost("/", CreateAsync).WithName("CreateSkill");
        skills.MapGet("/", ListAsync).WithName("ListSkills");

        var technicianSkills = app.MapGroup("/api/technicians/{technicianId:guid}/skills").WithTags("Technician skills");
        technicianSkills.MapPost("/{skillId:guid}", AssignAsync).WithName("AssignTechnicianSkill");
        technicianSkills.MapDelete("/{skillId:guid}", RemoveAsync).WithName("RemoveTechnicianSkill");
        technicianSkills.MapGet("/", ListForTechnicianAsync).WithName("ListTechnicianSkills");

        return app;
    }

    private static async Task<Results<Created<SkillResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateSkillRequest request, SkillService skills, CancellationToken cancellationToken)
    {
        var outcome = await skills.CreateAsync(new CreateSkill(request.Name, request.Code, request.IsActive), cancellationToken);

        return outcome switch
        {
            CreateSkillOutcome.Created created => TypedResults.Created((string?)null, created.Skill.ToResponse()),
            CreateSkillOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            CreateSkillOutcome.CodeAlreadyExists duplicate => ApiProblems.Conflict(
                "Skill code already exists", $"A skill with code '{duplicate.Code}' already exists."),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static async Task<Ok<SkillResponse[]>> ListAsync(SkillService skills, CancellationToken cancellationToken)
    {
        var list = await skills.ListAsync(cancellationToken);
        return TypedResults.Ok(list.Select(skill => skill.ToResponse()).ToArray());
    }

    /// <summary>Idempotent: 201 when the skill is newly assigned, 200 when it already was. Never a duplicate link.</summary>
    private static async Task<Results<Created<TechnicianSkillResponse>, Ok<TechnicianSkillResponse>, ProblemHttpResult>> AssignAsync(
        Guid technicianId, Guid skillId, SkillService skills, CancellationToken cancellationToken)
    {
        var outcome = await skills.AssignToTechnicianAsync(technicianId, skillId, cancellationToken);

        return outcome switch
        {
            AssignSkillOutcome.Assigned assigned => TypedResults.Created((string?)null, assigned.Skill.ToTechnicianSkillResponse(technicianId)),
            AssignSkillOutcome.AlreadyAssigned existing => TypedResults.Ok(existing.Skill.ToTechnicianSkillResponse(technicianId)),
            AssignSkillOutcome.TechnicianNotFound notFound => ApiProblems.TechnicianNotFound(notFound.TechnicianId),
            AssignSkillOutcome.SkillNotFound notFound => ApiProblems.SkillNotFound(notFound.SkillId),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    /// <summary>Idempotent: 204 also when the technician did not have the skill.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveAsync(
        Guid technicianId, Guid skillId, SkillService skills, CancellationToken cancellationToken)
    {
        var outcome = await skills.RemoveFromTechnicianAsync(technicianId, skillId, cancellationToken);

        return outcome switch
        {
            RemoveSkillOutcome.Removed => TypedResults.NoContent(),
            RemoveSkillOutcome.TechnicianNotFound notFound => ApiProblems.TechnicianNotFound(notFound.TechnicianId),
            RemoveSkillOutcome.SkillNotFound notFound => ApiProblems.SkillNotFound(notFound.SkillId),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static async Task<Results<Ok<TechnicianSkillResponse[]>, ProblemHttpResult>> ListForTechnicianAsync(
        Guid technicianId, SkillService skills, CancellationToken cancellationToken)
    {
        var list = await skills.ListForTechnicianAsync(technicianId, cancellationToken);

        return list is null
            ? ApiProblems.TechnicianNotFound(technicianId)
            : TypedResults.Ok(list.Select(skill => skill.ToTechnicianSkillResponse(technicianId)).ToArray());
    }

    internal static SkillResponse ToResponse(this Skill skill) => new(skill.Id, skill.Name, skill.Code, skill.IsActive);

    private static TechnicianSkillResponse ToTechnicianSkillResponse(this Skill skill, Guid technicianId) =>
        new(technicianId, skill.Id, skill.Name, skill.Code);
}
