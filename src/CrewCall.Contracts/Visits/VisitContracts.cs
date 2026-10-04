namespace CrewCall.Contracts.Visits;

/// <summary>The work order comes from the route: POST /api/work-orders/{workOrderId}/visits.</summary>
public sealed record CreateVisitRequest(DateTimeOffset? Start, DateTimeOffset? End, string? Notes);

/// <param name="Status">Target status: InProgress, Completed or Cancelled.</param>
public sealed record ChangeVisitStatusRequest(string? Status);

public sealed record VisitResponse(
    Guid Id,
    Guid WorkOrderId,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Status,
    string? Notes,
    DateTimeOffset CreatedAtUtc);
