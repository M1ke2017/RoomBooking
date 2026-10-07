using System.Text.Json;
using CrewCall.Persistence;
using CrewCall.WorkOrders.Executions;
using CrewCall.WorkOrders.Operations;
using CrewCall.WorkOrders.Visits;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static CrewCall.WorkOrders.Tests.WorkOrdersTestData;

namespace CrewCall.WorkOrders.Tests;

/// <summary>Field work on a real database with a controlled clock (ADR-0013).</summary>
public sealed class VisitExecutionServiceTests(WorkOrdersDatabase database)
{
    private static readonly DateTimeOffset Morning = new(2036, 5, 4, 8, 30, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private async Task<Guid> NewVisitAsync()
    {
        await using var scope = database.CreateScope();
        var workOrder = await CreateWorkOrderAsync(scope);
        return (await CreateVisitAsync(scope, workOrder.Id)).Id;
    }

    /// <summary>One operation in its own scope, like one HTTP request.</summary>
    private static async Task<VisitExecutionOutcome> RunAsync(
        ServiceProvider provider, Func<VisitExecutionService, Task<VisitExecutionOutcome>> operation)
    {
        await using var scope = provider.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<VisitExecutionService>());
    }

    private static async Task<VisitExecution> ChangedAsync(
        ServiceProvider provider, Func<VisitExecutionService, Task<VisitExecutionOutcome>> operation) =>
        Assert.IsType<VisitExecutionOutcome.Changed>(await RunAsync(provider, operation)).Execution;

    private static async Task<VisitExecution?> LoadAsync(ServiceProvider provider, Guid visitId)
    {
        await using var scope = provider.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<VisitExecutionService>().GetAsync(visitId, Cancellation)).Execution;
    }

    private async Task<List<(string Type, JsonElement Payload)>> VisitEventsAsync(Guid visitId)
    {
        await using var scope = database.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().OperationalEvents
            .AsNoTracking()
            .Where(e => e.AggregateType == WorkOrderEvents.VisitAggregate && e.AggregateId == visitId)
            .OrderBy(e => e.Sequence)
            .Select(e => new { e.EventType, e.PayloadJson })
            .ToListAsync(Cancellation);
        return events.Select(e => (e.EventType, JsonDocument.Parse(e.PayloadJson).RootElement)).ToList();
    }

    private async Task<int> ExecutionEventCountAsync(Guid visitId) =>
        (await VisitEventsAsync(visitId)).Count(e => e.Type is not (WorkOrderEvents.VisitCreated or WorkOrderEvents.VisitStatusChanged));

    private async Task<VisitStatus> VisitStatusAsync(Guid visitId)
    {
        await using var scope = database.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<VisitService>().GetAsync(visitId, Cancellation))!.Status;
    }

    private async Task<int> PauseRowsAsync(Guid visitId)
    {
        await using var scope = database.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrewCallDbContext>();
        return await db.VisitExecutionPauses.CountAsync(
            pause => db.VisitExecutions.Any(execution => execution.Id == pause.VisitExecutionId && execution.VisitId == visitId), Cancellation);
    }

    // ---- Lifecycle -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(FieldWorkStatus.NotStarted, FieldWorkStatus.Traveling)]
    [InlineData(FieldWorkStatus.NotStarted, FieldWorkStatus.Working)]
    [InlineData(FieldWorkStatus.NotStarted, FieldWorkStatus.Cancelled)]
    [InlineData(FieldWorkStatus.Traveling, FieldWorkStatus.Working)]
    [InlineData(FieldWorkStatus.Traveling, FieldWorkStatus.Cancelled)]
    [InlineData(FieldWorkStatus.Working, FieldWorkStatus.Paused)]
    [InlineData(FieldWorkStatus.Working, FieldWorkStatus.Completed)]
    [InlineData(FieldWorkStatus.Working, FieldWorkStatus.Cancelled)]
    [InlineData(FieldWorkStatus.Paused, FieldWorkStatus.Working)]
    [InlineData(FieldWorkStatus.Paused, FieldWorkStatus.Completed)]
    [InlineData(FieldWorkStatus.Paused, FieldWorkStatus.Cancelled)]
    public void Lifecycle_allows_exactly_the_documented_transitions(FieldWorkStatus from, FieldWorkStatus to)
    {
        Assert.True(FieldWorkLifecycle.CanTransition(from, to));

        var allowed = Enum.GetValues<FieldWorkStatus>()
            .SelectMany(source => Enum.GetValues<FieldWorkStatus>().Select(target => (source, target)))
            .Count(pair => FieldWorkLifecycle.CanTransition(pair.source, pair.target));
        Assert.Equal(11, allowed);
    }

    [Theory]
    [InlineData(FieldWorkStatus.Completed)]
    [InlineData(FieldWorkStatus.Cancelled)]
    public void Completed_and_cancelled_are_terminal(FieldWorkStatus terminal)
    {
        Assert.True(FieldWorkLifecycle.IsTerminal(terminal));
        Assert.All(Enum.GetValues<FieldWorkStatus>(), target => Assert.False(FieldWorkLifecycle.CanTransition(terminal, target)));
    }

    [Fact]
    public async Task Travel_work_pause_resume_complete_records_actual_times_metrics_and_events()
    {
        var clock = new ManualClock(Morning); // 08:30
        await using var provider = database.CreateProvider(clock);
        var visitId = await NewVisitAsync();

        var traveling = await ChangedAsync(provider, s => s.StartTravelAsync(visitId, Cancellation));
        clock.Set(Morning.AddMinutes(30)); // 09:00
        var working = await ChangedAsync(provider, s => s.StartWorkAsync(visitId, Cancellation));
        Assert.Equal(VisitStatus.InProgress, await VisitStatusAsync(visitId));
        clock.Set(Morning.AddMinutes(90)); // 10:00
        var paused = await ChangedAsync(provider, s => s.PauseAsync(visitId, Cancellation));
        clock.Set(Morning.AddMinutes(105)); // 10:15
        var resumed = await ChangedAsync(provider, s => s.ResumeAsync(visitId, Cancellation));
        clock.Set(Morning.AddMinutes(150)); // 11:00
        var completed = await ChangedAsync(provider, s => s.CompleteAsync(visitId, Cancellation));

        Assert.Equal(
            [FieldWorkStatus.Traveling, FieldWorkStatus.Working, FieldWorkStatus.Paused, FieldWorkStatus.Working, FieldWorkStatus.Completed],
            [traveling.Status, working.Status, paused.Status, resumed.Status, completed.Status]);
        Assert.Equal(new[] { traveling.Id, working.Id, paused.Id, resumed.Id }.Distinct(), [completed.Id]);

        var stored = (await LoadAsync(provider, visitId))!;
        Assert.Equal((Morning, Morning.AddMinutes(30), Morning.AddMinutes(150)), (stored.TravelStartedAtUtc, stored.WorkStartedAtUtc, stored.CompletedAtUtc));
        var pause = Assert.Single(stored.Pauses);
        Assert.Equal((Morning.AddMinutes(90), (DateTimeOffset?)Morning.AddMinutes(105)), (pause.StartedAtUtc, pause.EndedAtUtc));

        var metrics = VisitExecutionMetrics.For(stored);
        Assert.Equal(
            (TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(120), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(105), true),
            (metrics.Travel!.Value, metrics.GrossWork!.Value, metrics.Pause, metrics.NetWork!.Value, metrics.IsFinal));

        // The plan is untouched; the visit followed the work through its own lifecycle.
        Assert.Equal(VisitStatus.Completed, await VisitStatusAsync(visitId));

        var events = await VisitEventsAsync(visitId);
        Assert.Equal(
            [VisitExecutionEvents.VisitTravelStarted, VisitExecutionEvents.VisitWorkStarted, VisitExecutionEvents.VisitWorkPaused,
             VisitExecutionEvents.VisitWorkResumed, VisitExecutionEvents.VisitWorkCompleted],
            events.Select(e => e.Type).Where(type => type.StartsWith("VisitWork", StringComparison.Ordinal) || type == VisitExecutionEvents.VisitTravelStarted));
        Assert.Equal(
            [WorkOrderEvents.VisitCreated, WorkOrderEvents.VisitStatusChanged, WorkOrderEvents.VisitStatusChanged],
            events.Select(e => e.Type).Where(type => !type.StartsWith("VisitWork", StringComparison.Ordinal) && type != VisitExecutionEvents.VisitTravelStarted));
        var completedPayload = events.Single(e => e.Type == VisitExecutionEvents.VisitWorkCompleted).Payload;
        Assert.Equal(
            (30m, 120m, 15m, 105m),
            (completedPayload.GetProperty("travelDurationMinutes").GetDecimal(), completedPayload.GetProperty("grossWorkMinutes").GetDecimal(),
             completedPayload.GetProperty("pauseMinutes").GetDecimal(), completedPayload.GetProperty("netWorkMinutes").GetDecimal()));
        var pausedPayload = events.Single(e => e.Type == VisitExecutionEvents.VisitWorkPaused).Payload;
        Assert.Equal((pause.Id, stored.Id, visitId), (pausedPayload.GetProperty("pauseId").GetGuid(),
            pausedPayload.GetProperty("executionId").GetGuid(), pausedPayload.GetProperty("visitId").GetGuid()));
        Assert.Equal(Morning.AddMinutes(90), pausedPayload.GetProperty("occurredAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task Work_can_start_without_travel_and_then_has_no_travel_duration()
    {
        var clock = new ManualClock(Morning);
        await using var provider = database.CreateProvider(clock);
        var visitId = await NewVisitAsync();

        var working = await ChangedAsync(provider, s => s.StartWorkAsync(visitId, Cancellation));
        clock.Advance(TimeSpan.FromMinutes(45));
        var completed = await ChangedAsync(provider, s => s.CompleteAsync(visitId, Cancellation));

        Assert.Null(working.TravelStartedAtUtc);
        Assert.Equal(Morning, working.WorkStartedAtUtc);
        var metrics = VisitExecutionMetrics.For(completed);
        Assert.Equal(((TimeSpan?)null, TimeSpan.FromMinutes(45), TimeSpan.Zero, TimeSpan.FromMinutes(45)),
            (metrics.Travel, metrics.GrossWork!.Value, metrics.Pause, metrics.NetWork!.Value));
    }

    [Fact]
    public async Task Several_pauses_are_kept_and_summed_and_resume_by_start_work_keeps_the_first_start()
    {
        var clock = new ManualClock(Morning);
        await using var provider = database.CreateProvider(clock);
        var visitId = await NewVisitAsync();

        await ChangedAsync(provider, s => s.StartWorkAsync(visitId, Cancellation));
        foreach (var (workMinutes, pauseMinutes) in new[] { (10, 5), (20, 7), (30, 3) })
        {
            clock.Advance(TimeSpan.FromMinutes(workMinutes));
            await ChangedAsync(provider, s => s.PauseAsync(visitId, Cancellation));
            clock.Advance(TimeSpan.FromMinutes(pauseMinutes));
            // The last resume goes through start-work: from Paused it is a resume.
            await ChangedAsync(provider, s => pauseMinutes == 3 ? s.StartWorkAsync(visitId, Cancellation) : s.ResumeAsync(visitId, Cancellation));
        }

        clock.Advance(TimeSpan.FromMinutes(5));
        var completed = await ChangedAsync(provider, s => s.CompleteAsync(visitId, Cancellation));

        Assert.Equal(3, completed.Pauses.Count);
        Assert.All(completed.Pauses, pause => Assert.NotNull(pause.EndedAtUtc));
        Assert.Equal(Morning, completed.WorkStartedAtUtc);
        var metrics = VisitExecutionMetrics.For(completed);
        Assert.Equal((TimeSpan.FromMinutes(80), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(65)),
            (metrics.GrossWork!.Value, metrics.Pause, metrics.NetWork!.Value));
        var events = (await VisitEventsAsync(visitId)).Select(e => e.Type).ToList();
        Assert.Equal(3, events.Count(type => type == VisitExecutionEvents.VisitWorkResumed));
        Assert.Single(events, type => type == VisitExecutionEvents.VisitWorkStarted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Completing_or_cancelling_while_paused_closes_the_open_pause_at_that_instant(bool complete)
    {
        var clock = new ManualClock(Morning);
        await using var provider = database.CreateProvider(clock);
        var visitId = await NewVisitAsync();
        await ChangedAsync(provider, s => s.StartWorkAsync(visitId, Cancellation));
        clock.Advance(TimeSpan.FromMinutes(20));
        await ChangedAsync(provider, s => s.PauseAsync(visitId, Cancellation));
        clock.Advance(TimeSpan.FromMinutes(10));

        var ended = await ChangedAsync(provider, s => complete ? s.CompleteAsync(visitId, Cancellation) : s.CancelAsync(visitId, Cancellation));

        var pause = Assert.Single(ended.Pauses);
        Assert.Equal(Morning.AddMinutes(30), pause.EndedAtUtc);
        Assert.Equal(complete ? FieldWorkStatus.Completed : FieldWorkStatus.Cancelled, ended.Status);
        Assert.Equal(Morning.AddMinutes(30), complete ? ended.CompletedAtUtc : ended.CancelledAtUtc);
        Assert.Null((await LoadAsync(provider, visitId))!.OpenPause);
        var metrics = VisitExecutionMetrics.For(ended);
        Assert.Equal((TimeSpan.FromMinutes(10), complete), (metrics.Pause, metrics.IsFinal));
        if (complete)
        {
            Assert.Equal(TimeSpan.FromMinutes(20), metrics.NetWork);
        }
        else
        {
            Assert.Null(metrics.NetWork); // a cancelled execution has no final work duration
            Assert.Equal(VisitStatus.InProgress, await VisitStatusAsync(visitId)); // cancel does not touch the visit
        }
    }

    [Fact]
    public async Task Invalid_steps_are_refused_and_terminal_states_stay_terminal()
    {
        var clock = new ManualClock(Morning);
        await using var provider = database.CreateProvider(clock);
        var visitId = await NewVisitAsync();
        var cancelledVisitId = await NewVisitAsync();

        // Nothing started: no pause, resume or completion.
        foreach (var action in new Func<VisitExecutionService, Task<VisitExecutionOutcome>>[]
                 {
                     s => s.PauseAsync(visitId, Cancellation), s => s.ResumeAsync(visitId, Cancellation), s => s.CompleteAsync(visitId, Cancellation)
                 })
        {
            Assert.Equal(FieldWorkStatus.NotStarted, Assert.IsType<VisitExecutionOutcome.TransitionNotAllowed>(await RunAsync(provider, action)).From);
        }

        Assert.Null(await LoadAsync(provider, visitId)); // refused steps create nothing

        await ChangedAsync(provider, s => s.StartWorkAsync(visitId, Cancellation));
        clock.Advance(TimeSpan.FromMinutes(1));
        await ChangedAsync(provider, s => s.PauseAsync(visitId, Cancellation));
        Assert.IsType<VisitExecutionOutcome.TransitionNotAllowed>(await RunAsync(provider, s => s.StartTravelAsync(visitId, Cancellation)));
        Assert.IsType<VisitExecutionOutcome.TransitionNotAllowed>(await RunAsync(provider, s => s.PauseAsync(visitId, Cancellation)));
        clock.Advance(TimeSpan.FromMinutes(1));
        await ChangedAsync(provider, s => s.CompleteAsync(visitId, Cancellation));

        foreach (var action in new Func<VisitExecutionService, Task<VisitExecutionOutcome>>[]
                 {
                     s => s.StartWorkAsync(visitId, Cancellation), s => s.ResumeAsync(visitId, Cancellation),
                     s => s.PauseAsync(visitId, Cancellation), s => s.CancelAsync(visitId, Cancellation)
                 })
        {
            var outcome = await RunAsync(provider, action);
            Assert.True(outcome is VisitExecutionOutcome.TransitionNotAllowed { From: FieldWorkStatus.Completed } or VisitExecutionOutcome.VisitClosed);
        }

        // Cancelled without anything started: the execution is recorded as Cancelled; travel cannot start afterwards.
        var cancelled = await ChangedAsync(provider, s => s.CancelAsync(cancelledVisitId, Cancellation));
        Assert.Equal((FieldWorkStatus.Cancelled, (DateTimeOffset?)Morning.AddMinutes(2)), (cancelled.Status, cancelled.CancelledAtUtc));
        Assert.Equal(FieldWorkStatus.Cancelled,
            Assert.IsType<VisitExecutionOutcome.TransitionNotAllowed>(await RunAsync(provider, s => s.StartTravelAsync(cancelledVisitId, Cancellation))).From);
    }

    [Fact]
    public async Task Travel_and_work_need_an_existing_open_assigned_visit()
    {
        await using var provider = database.CreateProvider(new ManualClock(Morning));
        var unassigned = await NewVisitAsync();
        database.Assignments.MarkUnassigned(unassigned);
        var cancelledVisit = await NewVisitAsync();
        await using (var scope = database.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<VisitService>()
                .ChangeStatusAsync(new ChangeVisitStatus(cancelledVisit, "Cancelled"), Cancellation);
        }

        Assert.IsType<VisitExecutionOutcome.NoActiveAssignment>(await RunAsync(provider, s => s.StartTravelAsync(unassigned, Cancellation)));
        Assert.IsType<VisitExecutionOutcome.NoActiveAssignment>(await RunAsync(provider, s => s.StartWorkAsync(unassigned, Cancellation)));
        Assert.IsType<VisitExecutionOutcome.VisitClosed>(await RunAsync(provider, s => s.StartWorkAsync(cancelledVisit, Cancellation)));
        Assert.IsType<VisitExecutionOutcome.VisitNotFound>(await RunAsync(provider, s => s.StartTravelAsync(Guid.NewGuid(), Cancellation)));
        Assert.IsType<VisitExecutionOutcome.VisitNotFound>(await RunAsync(provider, s => s.PauseAsync(Guid.NewGuid(), Cancellation)));
        Assert.Null(await LoadAsync(provider, unassigned));
    }

    // ---- Concurrency ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("start-work, nothing recorded yet")]
    [InlineData("start-work while traveling")]
    [InlineData("pause twice")]
    [InlineData("complete and pause")]
    public async Task Of_two_concurrent_changes_exactly_one_wins_and_the_other_saves_nothing(string race)
    {
        var clock = new BarrierClock(Morning.AddHours(1));
        await using var provider = database.CreateProvider(clock);
        var visitId = await NewVisitAsync();
        await using (var setupProvider = database.CreateProvider(new ManualClock(Morning)))
        {
            if (race == "start-work while traveling")
            {
                await ChangedAsync(setupProvider, s => s.StartTravelAsync(visitId, Cancellation));
            }
            else if (race is "pause twice" or "complete and pause")
            {
                await ChangedAsync(setupProvider, s => s.StartWorkAsync(visitId, Cancellation));
            }
        }

        var executionEventsBefore = await ExecutionEventCountAsync(visitId);
        Func<VisitExecutionService, Task<VisitExecutionOutcome>> first = race switch
        {
            "pause twice" => s => s.PauseAsync(visitId, Cancellation),
            "complete and pause" => s => s.CompleteAsync(visitId, Cancellation),
            _ => s => s.StartWorkAsync(visitId, Cancellation)
        };
        Func<VisitExecutionService, Task<VisitExecutionOutcome>> second = race == "start-work, nothing recorded yet" || race == "start-work while traveling"
            ? s => s.StartWorkAsync(visitId, Cancellation)
            : s => s.PauseAsync(visitId, Cancellation);

        clock.Arm(2); // both load the same state, then both try to save
        var outcomes = await Task.WhenAll(Task.Run(() => RunAsync(provider, first)), Task.Run(() => RunAsync(provider, second)));

        Assert.Single(outcomes, outcome => outcome is VisitExecutionOutcome.Changed);
        Assert.Single(outcomes, outcome => outcome is VisitExecutionOutcome.ConcurrentChange);
        var winner = ((VisitExecutionOutcome.Changed)outcomes.Single(o => o is VisitExecutionOutcome.Changed)).Execution;
        var stored = (await LoadAsync(provider, visitId))!;
        Assert.Equal(winner.Status, stored.Status);
        Assert.Equal(stored.Status == FieldWorkStatus.Paused ? 1 : 0, await PauseRowsAsync(visitId));
        Assert.Equal(executionEventsBefore + 1, await ExecutionEventCountAsync(visitId)); // only the winner's event
    }

    // ---- Database guards -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("second execution for the visit", "23505")]
    [InlineData("second open pause", "23505")]
    [InlineData("pause ending before it starts", "23514")]
    [InlineData("completed without a completion time", "23514")]
    [InlineData("working without a work start", "23514")]
    [InlineData("unknown status", "23514")]
    [InlineData("work before travel", "23514")]
    public async Task The_database_rejects_inconsistent_field_work(string violation, string sqlState)
    {
        var clock = new ManualClock(Morning);
        await using var provider = database.CreateProvider(clock);
        var visitId = await NewVisitAsync();
        await ChangedAsync(provider, s => s.StartTravelAsync(visitId, Cancellation));
        clock.Advance(TimeSpan.FromMinutes(5));
        var execution = await ChangedAsync(provider, s => s.StartWorkAsync(visitId, Cancellation));
        clock.Advance(TimeSpan.FromMinutes(5));
        await ChangedAsync(provider, s => s.PauseAsync(visitId, Cancellation));

        var id = execution.Id;
        var sql = violation switch
        {
            "second execution for the visit" =>
                $"INSERT INTO workorders.visit_executions (id, visit_id, status, created_at_utc, updated_at_utc) VALUES ('{Guid.NewGuid()}', '{visitId}', 'NotStarted', now(), now())",
            "second open pause" =>
                $"INSERT INTO workorders.visit_execution_pauses (id, visit_execution_id, started_at_utc) VALUES ('{Guid.NewGuid()}', '{id}', now())",
            "pause ending before it starts" =>
                $"UPDATE workorders.visit_execution_pauses SET ended_at_utc = started_at_utc - interval '1 minute' WHERE visit_execution_id = '{id}'",
            "completed without a completion time" => $"UPDATE workorders.visit_executions SET status = 'Completed' WHERE id = '{id}'",
            "working without a work start" => $"UPDATE workorders.visit_executions SET work_started_at_utc = NULL WHERE id = '{id}'",
            "unknown status" => $"UPDATE workorders.visit_executions SET status = 'Driving' WHERE id = '{id}'",
            _ => $"UPDATE workorders.visit_executions SET travel_started_at_utc = work_started_at_utc + interval '1 minute' WHERE id = '{id}'"
        };

        await using var scope = database.CreateScope();
#pragma warning disable EF1002 // test-only SQL built from Guids
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().Database.ExecuteSqlRawAsync(sql, Cancellation));
#pragma warning restore EF1002
        Assert.Equal(sqlState, exception.SqlState);
    }
}
