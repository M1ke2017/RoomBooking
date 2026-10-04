using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

/// <summary>Problem details for the non-validation errors the modules report.</summary>
internal static class ApiProblems
{
    public static ProblemHttpResult NotFound(string title, string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: title, detail: detail);

    public static ProblemHttpResult Conflict(string title, string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: title, detail: detail);

    public static ProblemHttpResult TechnicianNotFound(Guid technicianId) =>
        NotFound("Technician not found", $"Technician '{technicianId}' does not exist.");

    public static ProblemHttpResult SkillNotFound(Guid skillId) =>
        NotFound("Skill not found", $"Skill '{skillId}' does not exist.");

    public static ProblemHttpResult TeamNotFound(Guid teamId) =>
        NotFound("Team not found", $"Team '{teamId}' does not exist.");
}
