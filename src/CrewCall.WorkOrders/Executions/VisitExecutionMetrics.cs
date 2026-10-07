namespace CrewCall.WorkOrders.Executions;

/// <summary>
/// Durations derived from an execution's timestamps; never stored (ADR-0013). Pure: the same timestamps always give the
/// same metrics, whatever the time of the call.
/// </summary>
/// <param name="Travel">WorkStarted − TravelStarted, when both exist (also before completion).</param>
/// <param name="GrossWork">Completed − WorkStarted; only for a Completed execution.</param>
/// <param name="Pause">The sum of the closed pauses (an open pause has no length yet).</param>
/// <param name="NetWork">GrossWork − Pause; only for a Completed execution.</param>
/// <param name="IsFinal">True only for a Completed execution: travel and pauses can still change otherwise.</param>
public sealed record VisitExecutionMetrics(TimeSpan? Travel, TimeSpan? GrossWork, TimeSpan Pause, TimeSpan? NetWork, bool IsFinal)
{
    public static readonly VisitExecutionMetrics None = new(null, null, TimeSpan.Zero, null, false);

    public static VisitExecutionMetrics Calculate(
        FieldWorkStatus status,
        DateTimeOffset? travelStartedAtUtc,
        DateTimeOffset? workStartedAtUtc,
        DateTimeOffset? completedAtUtc,
        IEnumerable<(DateTimeOffset StartedAtUtc, DateTimeOffset? EndedAtUtc)> pauses)
    {
        TimeSpan? travel = travelStartedAtUtc is not null && workStartedAtUtc is not null
            ? workStartedAtUtc.Value - travelStartedAtUtc.Value
            : null;

        var pause = TimeSpan.Zero;
        foreach (var (started, ended) in pauses)
        {
            if (ended is { } end)
            {
                pause += end - started;
            }
        }

        if (status != FieldWorkStatus.Completed || workStartedAtUtc is null || completedAtUtc is null)
        {
            return new VisitExecutionMetrics(travel, null, pause, null, false);
        }

        var gross = completedAtUtc.Value - workStartedAtUtc.Value;
        return new VisitExecutionMetrics(travel, gross, pause, gross - pause, true);
    }

    public static VisitExecutionMetrics For(VisitExecution execution) =>
        Calculate(
            execution.Status,
            execution.TravelStartedAtUtc,
            execution.WorkStartedAtUtc,
            execution.CompletedAtUtc,
            execution.Pauses.Select(pause => (pause.StartedAtUtc, pause.EndedAtUtc)));

    /// <summary>Minutes with two decimals, for responses and event payloads.</summary>
    public static decimal Minutes(TimeSpan duration) => Math.Round((decimal)duration.TotalMinutes, 2);

    public static decimal? Minutes(TimeSpan? duration) => duration is { } value ? Minutes(value) : null;
}
