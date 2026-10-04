using CrewCall.Contracts.Scheduling;
using CrewCall.Scheduling.Assignments;
using CrewCall.Scheduling.Checks;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

internal static class AssignmentEndpoints
{
    public static IEndpointRouteBuilder MapAssignmentEndpoints(this IEndpointRouteBuilder app)
    {
        var visit = app.MapGroup("/api/visits/{visitId:guid}").WithTags("Assignments");
        visit.MapPost("/assignment", CreateAsync).WithName("CreateAssignment");
        visit.MapGet("/assignment", GetActiveAsync).WithName("GetActiveAssignment");
        visit.MapGet("/assignments/history", GetHistoryAsync).WithName("GetAssignmentHistory");
        visit.MapPost("/assignment/reassign", ReassignAsync).WithName("ReassignVisit");
        visit.MapDelete("/assignment", CancelAsync).WithName("CancelAssignment");

        return app;
    }

    /// <summary>201 with the new active assignment; 409 when the final check fails or a race is lost.</summary>
    private static async Task<Results<Created<AssignmentResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid visitId, CreateAssignmentRequest request, AssignmentService assignments, CancellationToken cancellationToken)
    {
        var outcome = await assignments.CreateAsync(
            new CreateAssignment(
                visitId, request.TechnicianId, request.VehicleId, request.EquipmentIds, request.RequiredSkillCodes,
                request.TravelBufferBeforeMinutes, request.TravelBufferAfterMinutes),
            cancellationToken);

        return outcome switch
        {
            AssignOutcome.Assigned assigned => TypedResults.Created($"/api/visits/{visitId}/assignment", assigned.Assignment.ToResponse()),
            AssignOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            _ => ToProblem(outcome)
        };
    }

    /// <summary>
    /// 200 with the new active assignment (the visit's assignment resource is updated; the old one stays in history as
    /// Replaced). 404 when the visit has no active assignment.
    /// </summary>
    private static async Task<Results<Ok<AssignmentResponse>, ValidationProblem, ProblemHttpResult>> ReassignAsync(
        Guid visitId, ReassignAssignmentRequest request, AssignmentService assignments, CancellationToken cancellationToken)
    {
        var outcome = await assignments.ReassignAsync(
            new ReassignVisit(
                visitId, request.TechnicianId, request.VehicleId, request.EquipmentIds, request.RequiredSkillCodes,
                request.TravelBufferBeforeMinutes, request.TravelBufferAfterMinutes),
            cancellationToken);

        return outcome switch
        {
            AssignOutcome.Assigned assigned => TypedResults.Ok(assigned.Assignment.ToResponse()),
            AssignOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            _ => ToProblem(outcome)
        };
    }

    private static async Task<Results<Ok<AssignmentResponse>, ProblemHttpResult>> GetActiveAsync(
        Guid visitId, AssignmentService assignments, CancellationToken cancellationToken)
    {
        var (visitFound, active) = await assignments.GetActiveForVisitAsync(visitId, cancellationToken);

        if (!visitFound)
        {
            return VisitNotFound(visitId);
        }

        return active is null
            ? ApiProblems.NotFound("No active assignment", $"Visit '{visitId}' has no active assignment.")
            : TypedResults.Ok(active.ToResponse());
    }

    private static async Task<Results<Ok<AssignmentHistoryResponse>, ProblemHttpResult>> GetHistoryAsync(
        Guid visitId, AssignmentService assignments, CancellationToken cancellationToken)
    {
        var history = await assignments.GetHistoryForVisitAsync(visitId, cancellationToken);

        return history is null
            ? VisitNotFound(visitId)
            : TypedResults.Ok(new AssignmentHistoryResponse(visitId, history.Select(assignment => assignment.ToResponse()).ToArray()));
    }

    /// <summary>204 when cancelled, and also when there was no active assignment (idempotent); 404 for a missing visit.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> CancelAsync(
        Guid visitId, AssignmentService assignments, CancellationToken cancellationToken)
    {
        var outcome = await assignments.CancelAsync(visitId, cancellationToken);

        return outcome switch
        {
            CancelAssignmentOutcome.Cancelled or CancelAssignmentOutcome.NothingToCancel => TypedResults.NoContent(),
            CancelAssignmentOutcome.VisitNotFound => VisitNotFound(visitId),
            CancelAssignmentOutcome.ConcurrentChange => ApiProblems.Conflict(
                "Assignment changed concurrently", $"The assignment of visit '{visitId}' was changed by another request. Retry."),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    /// <summary>404 for missing visit/resources/active assignment; 409 for every conflict (closed visit, rejected check, lost race).</summary>
    private static ProblemHttpResult ToProblem(AssignOutcome outcome) => outcome switch
    {
        AssignOutcome.VisitNotFound notFound => VisitNotFound(notFound.VisitId),
        AssignOutcome.ResourcesNotFound notFound => ApiProblems.NotFound(
            "Resource not found",
            "These resources do not exist: "
            + string.Join(", ", notFound.Missing.Select(resource => $"{resource.Type} '{resource.ResourceId}'")) + "."),
        AssignOutcome.NoActiveAssignment none => ApiProblems.NotFound(
            "No active assignment", $"Visit '{none.VisitId}' has no active assignment to replace."),
        AssignOutcome.VisitClosed closed => ApiProblems.Conflict(
            "Visit closed", $"Visit '{closed.VisitId}' is {closed.VisitStatus} and cannot be assigned."),
        AssignOutcome.AlreadyAssigned assigned => ApiProblems.Conflict(
            "Visit already assigned",
            $"Visit '{assigned.VisitId}' already has active assignment '{assigned.ActiveAssignmentId}'. Use reassign."),
        AssignOutcome.ConcurrentChange changed => ApiProblems.Conflict(
            "Assignment changed concurrently", $"The assignment of visit '{changed.VisitId}' was changed by another request. Retry."),
        AssignOutcome.Rejected rejected => TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Resources not available",
            detail: string.Join(" ", rejected.Reasons.Select(reason => reason.Message)),
            extensions: new Dictionary<string, object?> { ["reasons"] = rejected.Reasons.Select(SchedulingEndpoints.ToConflictResponse).ToArray() }),
        _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
    };

    private static ProblemHttpResult VisitNotFound(Guid visitId) =>
        ApiProblems.NotFound("Visit not found", $"Visit '{visitId}' does not exist.");

    private static AssignmentResponse ToResponse(this Assignment assignment) =>
        new(
            assignment.Id,
            assignment.VisitId,
            assignment.TechnicianId,
            assignment.VehicleId,
            assignment.Equipment.Select(equipment => equipment.EquipmentId).Order().ToArray(),
            assignment.TravelBufferBeforeMinutes,
            assignment.TravelBufferAfterMinutes,
            assignment.Status.ToString(),
            assignment.ClaimedStart,
            assignment.ClaimedEnd,
            assignment.ReplacedByAssignmentId,
            assignment.CreatedAtUtc,
            assignment.UpdatedAtUtc);
}
