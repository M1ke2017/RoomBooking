namespace CrewCall.Contracts.WorkOrders;

/// <param name="Priority">Low, Normal, High or Urgent (case-insensitive); Normal when omitted.</param>
public sealed record CreateWorkOrderRequest(Guid CustomerId, Guid SiteId, string? Title, string? Description, string? Priority);

/// <param name="Status">Target status: Planned, InProgress, Completed or Cancelled.</param>
public sealed record ChangeWorkOrderStatusRequest(string? Status);

public sealed record WorkOrderResponse(
    Guid Id,
    Guid CustomerId,
    Guid SiteId,
    string Title,
    string? Description,
    string Priority,
    string Status,
    DateTimeOffset CreatedAtUtc);
