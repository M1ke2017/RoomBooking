using CrewCall.Contracts.WorkOrders;
using CrewCall.WorkOrders;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

internal static class WorkOrderEndpoints
{
    public static IEndpointRouteBuilder MapWorkOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var workOrders = app.MapGroup("/api/work-orders").WithTags("Work orders");
        workOrders.MapPost("/", CreateAsync).WithName("CreateWorkOrder");
        workOrders.MapGet("/", ListAsync).WithName("ListWorkOrders");
        workOrders.MapGet("/{id:guid}", GetAsync).WithName("GetWorkOrder");
        workOrders.MapPost("/{id:guid}/status", ChangeStatusAsync).WithName("ChangeWorkOrderStatus");

        return app;
    }

    private static async Task<Results<Created<WorkOrderResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateWorkOrderRequest request, WorkOrderService workOrders, CancellationToken cancellationToken)
    {
        var outcome = await workOrders.CreateAsync(
            new CreateWorkOrder(request.CustomerId, request.SiteId, request.Title, request.Description, request.Priority), cancellationToken);

        return outcome switch
        {
            CreateWorkOrderOutcome.Created created => TypedResults.Created($"/api/work-orders/{created.WorkOrder.Id}", created.WorkOrder.ToResponse()),
            CreateWorkOrderOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            CreateWorkOrderOutcome.CustomerNotFound notFound => ApiProblems.NotFound("Customer not found", $"Customer '{notFound.CustomerId}' does not exist."),
            CreateWorkOrderOutcome.SiteNotFound notFound => ApiProblems.NotFound("Site not found", $"Site '{notFound.SiteId}' does not exist."),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static async Task<Ok<WorkOrderResponse[]>> ListAsync(WorkOrderService workOrders, CancellationToken cancellationToken)
    {
        var list = await workOrders.ListAsync(cancellationToken);
        return TypedResults.Ok(list.Select(workOrder => workOrder.ToResponse()).ToArray());
    }

    private static async Task<Results<Ok<WorkOrderResponse>, ProblemHttpResult>> GetAsync(
        Guid id, WorkOrderService workOrders, CancellationToken cancellationToken)
    {
        var workOrder = await workOrders.GetAsync(id, cancellationToken);
        return workOrder is null ? WorkOrderNotFound(id) : TypedResults.Ok(workOrder.ToResponse());
    }

    private static async Task<Results<Ok<WorkOrderResponse>, ValidationProblem, ProblemHttpResult>> ChangeStatusAsync(
        Guid id, ChangeWorkOrderStatusRequest request, WorkOrderService workOrders, CancellationToken cancellationToken)
    {
        var outcome = await workOrders.ChangeStatusAsync(new ChangeWorkOrderStatus(id, request.Status), cancellationToken);

        return outcome switch
        {
            ChangeWorkOrderStatusOutcome.Changed changed => TypedResults.Ok(changed.WorkOrder.ToResponse()),
            ChangeWorkOrderStatusOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            ChangeWorkOrderStatusOutcome.NotFound notFound => WorkOrderNotFound(notFound.WorkOrderId),
            ChangeWorkOrderStatusOutcome.TransitionNotAllowed notAllowed => ApiProblems.Conflict(
                "Status transition not allowed", $"A work order cannot move from {notAllowed.From} to {notAllowed.To}."),
            ChangeWorkOrderStatusOutcome.ConcurrentChange concurrent => ApiProblems.Conflict(
                "Concurrent change", $"Work order '{concurrent.WorkOrderId}' was changed by another request. Reload and retry."),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    internal static ProblemHttpResult WorkOrderNotFound(Guid workOrderId) =>
        ApiProblems.NotFound("Work order not found", $"Work order '{workOrderId}' does not exist.");

    private static WorkOrderResponse ToResponse(this WorkOrder workOrder) =>
        new(
            workOrder.Id,
            workOrder.CustomerId,
            workOrder.SiteId,
            workOrder.Title,
            workOrder.Description,
            workOrder.Priority.ToString(),
            workOrder.Status.ToString(),
            workOrder.CreatedAtUtc);
}
