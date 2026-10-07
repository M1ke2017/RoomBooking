using CrewCall.WorkOrders.Executions;
using Xunit;

namespace CrewCall.WorkOrders.Tests;

/// <summary>Durations are derived from the timestamps only (ADR-0013).</summary>
public sealed class VisitExecutionMetricsTests
{
    private static readonly DateTimeOffset T0 = new(2036, 1, 1, 8, 30, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int minutes) => T0.AddMinutes(minutes);

    private static (DateTimeOffset, DateTimeOffset?) Pause(int from, int? to) => (At(from), to is { } end ? At(end) : null);

    [Fact]
    public void A_completed_execution_has_final_travel_gross_pause_and_net_durations()
    {
        var metrics = VisitExecutionMetrics.Calculate(FieldWorkStatus.Completed, At(0), At(30), At(150), [Pause(90, 105)]);

        Assert.Equal(
            (TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(120), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(105), true),
            (metrics.Travel!.Value, metrics.GrossWork!.Value, metrics.Pause, metrics.NetWork!.Value, metrics.IsFinal));
        Assert.Equal((30m, 120m, 15m, 105m),
            (VisitExecutionMetrics.Minutes(metrics.Travel)!.Value, VisitExecutionMetrics.Minutes(metrics.GrossWork)!.Value,
             VisitExecutionMetrics.Minutes(metrics.Pause), VisitExecutionMetrics.Minutes(metrics.NetWork)!.Value));
    }

    [Fact]
    public void Without_travel_there_is_no_travel_duration()
    {
        var metrics = VisitExecutionMetrics.Calculate(FieldWorkStatus.Completed, null, At(0), At(60), []);

        Assert.Null(metrics.Travel);
        Assert.Equal((TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60)), (metrics.GrossWork!.Value, metrics.NetWork!.Value));
    }

    [Theory]
    [InlineData(FieldWorkStatus.Working)]
    [InlineData(FieldWorkStatus.Paused)]
    [InlineData(FieldWorkStatus.Cancelled)]
    public void An_unfinished_execution_never_pretends_a_final_work_duration(FieldWorkStatus status)
    {
        var metrics = VisitExecutionMetrics.Calculate(status, At(0), At(10), null, [Pause(20, 25), Pause(40, null)]);

        Assert.False(metrics.IsFinal);
        Assert.Null(metrics.GrossWork);
        Assert.Null(metrics.NetWork);
        Assert.Equal(TimeSpan.FromMinutes(10), metrics.Travel); // known once work started
        Assert.Equal(TimeSpan.FromMinutes(5), metrics.Pause); // the open pause has no length yet
    }

    [Fact]
    public void Several_pauses_are_summed_and_subtracted()
    {
        var metrics = VisitExecutionMetrics.Calculate(
            FieldWorkStatus.Completed, null, At(0), At(80), [Pause(10, 15), Pause(35, 42), Pause(72, 75)]);

        Assert.Equal((TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(65)), (metrics.Pause, metrics.NetWork!.Value));
    }

    [Fact]
    public void Traveling_only_has_no_durations_yet()
    {
        Assert.Equal(
            new VisitExecutionMetrics(null, null, TimeSpan.Zero, null, false),
            VisitExecutionMetrics.Calculate(FieldWorkStatus.Traveling, At(0), null, null, []));
    }
}
