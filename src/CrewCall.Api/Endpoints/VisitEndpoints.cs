using CrewCall.Contracts.Visits;
using CrewCall.WorkOrders.Visits;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

internal static class VisitEndpoints
{
    public static IEndpointRouteBuilder MapVisitEndpoints(this IEndpointRouteBuilder app)
    {
        var workOrderVisits = app.MapGroup("/api/work-orders/{workOrderId:guid}/visits").WithTags("Visits");
        workOrderVisits.MapPost("/", CreateAsync).WithName("CreateVisit");
        workOrderVisits.MapGet("/", ListForWorkOrderAsync).WithName("ListWorkOrderVisits");

        var visits = app.MapGroup("/api/visits").WithTags("Visits");
        visits.MapGet("/{id:guid}", GetAsync).WithName("GetVisit");
        visits.MapPost("/{id:guid}/status", ChangeStatusAsync).WithName("ChangeVisitStatus");

        return app;
    }

    private static async Task<Results<Created<VisitResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid workOrderId, CreateVisitRequest request, VisitService visits, CancellationToken cancellationToken)
    {
        var outcome = await visits.CreateAsync(new CreateVisit(workOrderId, request.Start, request.End, request.Notes), cancellationToken);

        return outcome switch
        {
            CreateVisitOutcome.Created created => TypedResults.Created($"/api/visits/{created.Visit.Id}", created.Visit.ToResponse()),
            CreateVisitOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            CreateVisitOutcome.WorkOrderNotFound notFound => WorkOrderEndpoints.WorkOrderNotFound(notFound.WorkOrderId),
            CreateVisitOutcome.WorkOrderClosed closed => ApiProblems.Conflict(
                "Work order is closed", $"Work order '{closed.WorkOrderId}' is {closed.Status} and takes no new visits."),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static async Task<Results<Ok<VisitResponse[]>, ProblemHttpResult>> ListForWorkOrderAsync(
        Guid workOrderId, VisitService visits, CancellationToken cancellationToken)
    {
        var list = await visits.ListForWorkOrderAsync(workOrderId, cancellationToken);

        return list is null
            ? WorkOrderEndpoints.WorkOrderNotFound(workOrderId)
            : TypedResults.Ok(list.Select(visit => visit.ToResponse()).ToArray());
    }

    private static async Task<Results<Ok<VisitResponse>, ProblemHttpResult>> GetAsync(
        Guid id, VisitService visits, CancellationToken cancellationToken)
    {
        var visit = await visits.GetAsync(id, cancellationToken);
        return visit is null ? VisitNotFound(id) : TypedResults.Ok(visit.ToResponse());
    }

    private static async Task<Results<Ok<VisitResponse>, ValidationProblem, ProblemHttpResult>> ChangeStatusAsync(
        Guid id, ChangeVisitStatusRequest request, VisitService visits, CancellationToken cancellationToken)
    {
        var outcome = await visits.ChangeStatusAsync(new ChangeVisitStatus(id, request.Status), cancellationToken);

        return outcome switch
        {
            ChangeVisitStatusOutcome.Changed changed => TypedResults.Ok(changed.Visit.ToResponse()),
            ChangeVisitStatusOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            ChangeVisitStatusOutcome.NotFound notFound => VisitNotFound(notFound.VisitId),
            ChangeVisitStatusOutcome.TransitionNotAllowed notAllowed => ApiProblems.Conflict(
                "Status transition not allowed", $"A visit cannot move from {notAllowed.From} to {notAllowed.To}."),
            ChangeVisitStatusOutcome.ConcurrentChange concurrent => ApiProblems.Conflict(
                "Concurrent change", $"Visit '{concurrent.VisitId}' was changed by another request. Reload and retry."),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static ProblemHttpResult VisitNotFound(Guid visitId) =>
        ApiProblems.NotFound("Visit not found", $"Visit '{visitId}' does not exist.");

    private static VisitResponse ToResponse(this Visit visit) =>
        new(visit.Id, visit.WorkOrderId, visit.Start, visit.End, visit.Status.ToString(), visit.Notes, visit.CreatedAtUtc);
}
