using CrewCall.WorkOrders.Executions;

namespace CrewCall.WorkOrders.Operations;

/// <summary>
/// Field work events (ADR-0013). Recorded on the visit's own history (aggregate "visit"), so the log's sequence orders
/// the plan's and the execution's events together. Each is appended in the transaction of the change it describes.
/// </summary>
public static class VisitExecutionEvents
{
    public const string VisitTravelStarted = nameof(VisitTravelStarted);
    public const string VisitWorkStarted = nameof(VisitWorkStarted);
    public const string VisitWorkPaused = nameof(VisitWorkPaused);
    public const string VisitWorkResumed = nameof(VisitWorkResumed);
    public const string VisitWorkCompleted = nameof(VisitWorkCompleted);
    public const string VisitExecutionCancelled = nameof(VisitExecutionCancelled);
}

public sealed record VisitTravelStartedPayload(Guid VisitId, Guid ExecutionId, DateTimeOffset OccurredAtUtc);

public sealed record VisitWorkStartedPayload(Guid VisitId, Guid ExecutionId, DateTimeOffset OccurredAtUtc, decimal? TravelDurationMinutes);

public sealed record VisitWorkPausedPayload(Guid VisitId, Guid ExecutionId, Guid PauseId, DateTimeOffset OccurredAtUtc);

public sealed record VisitWorkResumedPayload(Guid VisitId, Guid ExecutionId, Guid PauseId, DateTimeOffset OccurredAtUtc, decimal PauseMinutes);

/// <param name="ClosedPauseId">The pause that completion ended, when the work was paused.</param>
public sealed record VisitWorkCompletedPayload(
    Guid VisitId,
    Guid ExecutionId,
    DateTimeOffset OccurredAtUtc,
    decimal? TravelDurationMinutes,
    decimal GrossWorkMinutes,
    decimal PauseMinutes,
    decimal NetWorkMinutes,
    Guid? ClosedPauseId);

public sealed record VisitExecutionCancelledPayload(
    Guid VisitId, Guid ExecutionId, DateTimeOffset OccurredAtUtc, FieldWorkStatus PreviousStatus, Guid? ClosedPauseId);
