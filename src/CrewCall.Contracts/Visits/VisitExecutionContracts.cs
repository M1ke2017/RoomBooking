namespace CrewCall.Contracts.Visits;

/// <summary>
/// GET /api/visits/{visitId}/execution and every field work transition: what actually happened on the visit. The
/// visit's planned Start/End are not part of it. A visit without recorded field work is NotStarted, with no ExecutionId.
/// </summary>
/// <param name="Status">NotStarted, Traveling, Working, Paused, Completed or Cancelled.</param>
/// <param name="WorkStartedAtUtc">The first start of work; resuming after a pause does not change it.</param>
/// <param name="Pauses">Oldest first; at most one is open (EndedAtUtc null), only while Paused.</param>
public sealed record VisitExecutionResponse(
    Guid? ExecutionId,
    Guid VisitId,
    string Status,
    DateTimeOffset? TravelStartedAtUtc,
    DateTimeOffset? WorkStartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? CancelledAtUtc,
    VisitExecutionPauseResponse[] Pauses,
    VisitExecutionMetricsResponse Metrics);

public sealed record VisitExecutionPauseResponse(Guid PauseId, DateTimeOffset StartedAtUtc, DateTimeOffset? EndedAtUtc);

/// <summary>Derived from the timestamps, never stored. Minutes with two decimals.</summary>
/// <param name="TravelMinutes">Work start − travel start, when both were recorded.</param>
/// <param name="GrossWorkMinutes">Completion − work start; only once Completed.</param>
/// <param name="PauseMinutes">Closed pauses only.</param>
/// <param name="NetWorkMinutes">Gross work − pauses; only once Completed.</param>
/// <param name="IsFinal">True only for a Completed execution.</param>
public sealed record VisitExecutionMetricsResponse(
    decimal? TravelMinutes, decimal? GrossWorkMinutes, decimal PauseMinutes, decimal? NetWorkMinutes, bool IsFinal);
