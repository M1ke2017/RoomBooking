using CrewCall.Contracts.Scheduling;
using CrewCall.Scheduling.Checks;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

internal static class SchedulingEndpoints
{
    public static IEndpointRouteBuilder MapSchedulingEndpoints(this IEndpointRouteBuilder app)
    {
        var scheduling = app.MapGroup("/api/scheduling").WithTags("Scheduling");
        scheduling.MapPost("/check", CheckAsync).WithName("CheckScheduling");

        return app;
    }

    /// <summary>
    /// 200 for every completed evaluation, feasible or not; 400 for an invalid request; 404 when a technician, vehicle or
    /// equipment asset does not exist. Never 409: the check reserves nothing.
    /// </summary>
    private static async Task<Results<Ok<SchedulingCheckResponse>, ValidationProblem, ProblemHttpResult>> CheckAsync(
        SchedulingCheckRequest request, SchedulingCheckService checks, CancellationToken cancellationToken)
    {
        var outcome = await checks.CheckAsync(
            new SchedulingCheck(
                request.VisitId,
                request.TechnicianId,
                request.VehicleId,
                request.EquipmentIds,
                request.Start,
                request.End,
                request.RequiredSkillCodes,
                request.TravelBufferBeforeMinutes,
                request.TravelBufferAfterMinutes),
            cancellationToken);

        return outcome switch
        {
            SchedulingCheckOutcome.Checked checkedOutcome => TypedResults.Ok(checkedOutcome.Result.ToResponse()),
            SchedulingCheckOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            SchedulingCheckOutcome.ResourcesNotFound notFound => ApiProblems.NotFound(
                "Resource not found",
                "These resources do not exist: "
                + string.Join(", ", notFound.Missing.Select(resource => $"{resource.Type} '{resource.ResourceId}'")) + "."),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static SchedulingCheckResponse ToResponse(this SchedulingCheckResult result) =>
        new(
            result.IsFeasible,
            result.Reasons.Select(reason => new SchedulingConflictResponse(
                reason.Code.ToString(),
                reason.Message,
                reason.ResourceType?.ToString(),
                reason.RelatedResourceId,
                reason.ReservationId,
                reason.ReservedStart,
                reason.ReservedEnd,
                reason.Details.ToArray())).ToArray(),
            result.TechnicianId,
            result.VehicleId,
            result.EquipmentIds.ToArray(),
            result.VisitId,
            result.Start,
            result.End,
            result.EffectiveStart,
            result.EffectiveEnd);
}
