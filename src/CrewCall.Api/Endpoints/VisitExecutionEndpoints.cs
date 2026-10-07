using CrewCall.Contracts.Visits;
using CrewCall.WorkOrders.Executions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

/// <summary>
/// Field work on a visit (ADR-0013). Every successful transition returns 200 with the whole execution, also the first
/// one that creates it: the execution is a sub-resource of the visit that always exists conceptually (NotStarted).
/// </summary>
internal static class VisitExecutionEndpoints
{
    public static IEndpointRouteBuilder MapVisitExecutionEndpoints(this IEndpointRouteBuilder app)
    {
        var execution = app.MapGroup("/api/visits/{visitId:guid}/execution").WithTags("Field work");
        execution.MapGet("/", GetAsync).WithName("GetVisitExecution");
        execution.MapPost("/start-travel", (Guid visitId, VisitExecutionService service, CancellationToken token) =>
            Transition(service.StartTravelAsync(visitId, token))).WithName("StartVisitTravel");
        execution.MapPost("/start-work", (Guid visitId, VisitExecutionService service, CancellationToken token) =>
            Transition(service.StartWorkAsync(visitId, token))).WithName("StartVisitWork");
        execution.MapPost("/pause", (Guid visitId, VisitExecutionService service, CancellationToken token) =>
            Transition(service.PauseAsync(visitId, token))).WithName("PauseVisitWork");
        execution.MapPost("/resume", (Guid visitId, VisitExecutionService service, CancellationToken token) =>
            Transition(service.ResumeAsync(visitId, token))).WithName("ResumeVisitWork");
        execution.MapPost("/complete", (Guid visitId, VisitExecutionService service, CancellationToken token) =>
            Transition(service.CompleteAsync(visitId, token))).WithName("CompleteVisitWork");
        execution.MapPost("/cancel", (Guid visitId, VisitExecutionService service, CancellationToken token) =>
            Transition(service.CancelAsync(visitId, token))).WithName("CancelVisitExecution");

        return app;
    }

    private static async Task<Results<Ok<VisitExecutionResponse>, ProblemHttpResult>> GetAsync(
        Guid visitId, VisitExecutionService service, CancellationToken cancellationToken)
    {
        var (visitFound, execution) = await service.GetAsync(visitId, cancellationToken);
        return visitFound ? TypedResults.Ok(ToResponse(visitId, execution)) : VisitNotFound(visitId);
    }

    /// <summary>200 with the execution; 404 for a missing visit; 409 for an invalid step, a missing assignment or a lost race.</summary>
    private static async Task<Results<Ok<VisitExecutionResponse>, ProblemHttpResult>> Transition(Task<VisitExecutionOutcome> operation)
    {
        var outcome = await operation;
        return outcome switch
        {
            VisitExecutionOutcome.Changed changed => TypedResults.Ok(ToResponse(changed.Execution.VisitId, changed.Execution)),
            VisitExecutionOutcome.VisitNotFound notFound => VisitNotFound(notFound.VisitId),
            VisitExecutionOutcome.VisitClosed closed => ApiProblems.Conflict(
                "Visit closed", $"Visit '{closed.VisitId}' is {closed.VisitStatus}; field work cannot start."),
            VisitExecutionOutcome.NoActiveAssignment none => ApiProblems.Conflict(
                "No active assignment", $"Visit '{none.VisitId}' has no active assignment; travel and work need assigned resources."),
            VisitExecutionOutcome.TransitionNotAllowed notAllowed => ApiProblems.Conflict(
                "Field work transition not allowed", $"{notAllowed.Action} is not allowed while field work is {notAllowed.From}."),
            VisitExecutionOutcome.ConcurrentChange concurrent => ApiProblems.Conflict(
                "Concurrent change", $"The field work of visit '{concurrent.VisitId}' was changed by another request. Reload and retry."),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static ProblemHttpResult VisitNotFound(Guid visitId) =>
        ApiProblems.NotFound("Visit not found", $"Visit '{visitId}' does not exist.");

    internal static VisitExecutionMetricsResponse ToResponse(VisitExecutionMetrics metrics) =>
        new(
            VisitExecutionMetrics.Minutes(metrics.Travel),
            VisitExecutionMetrics.Minutes(metrics.GrossWork),
            VisitExecutionMetrics.Minutes(metrics.Pause),
            VisitExecutionMetrics.Minutes(metrics.NetWork),
            metrics.IsFinal);

    private static VisitExecutionResponse ToResponse(Guid visitId, VisitExecution? execution) =>
        execution is null
            ? new(null, visitId, nameof(FieldWorkStatus.NotStarted), null, null, null, null, [], ToResponse(VisitExecutionMetrics.None))
            : new(
                execution.Id,
                execution.VisitId,
                execution.Status.ToString(),
                execution.TravelStartedAtUtc,
                execution.WorkStartedAtUtc,
                execution.CompletedAtUtc,
                execution.CancelledAtUtc,
                execution.Pauses
                    .OrderBy(pause => pause.StartedAtUtc)
                    .Select(pause => new VisitExecutionPauseResponse(pause.Id, pause.StartedAtUtc, pause.EndedAtUtc))
                    .ToArray(),
                ToResponse(VisitExecutionMetrics.For(execution)));
}
