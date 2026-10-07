using System.Net;
using System.Net.Http.Json;
using CrewCall.Contracts.OperationalCalendar;
using CrewCall.Contracts.Visits;
using CrewCall.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CrewCall.Api.Tests.IncidentTestKit;

namespace CrewCall.Api.Tests;

/// <summary>Field work over HTTP (ADR-0013): the execution endpoints, its events and the operational calendar.</summary>
public sealed class VisitExecutionEndpointsTests(CrewCallApiFactory factory)
{
    private static int _daySequence;

    private static DateTimeOffset NewWorkDay() =>
        new DateTimeOffset(2036, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(Interlocked.Increment(ref _daySequence));

    /// <summary>A visit 09:00–10:00 on <paramref name="day"/>, assigned to a new technician.</summary>
    private static async Task<(Guid VisitId, Guid TechnicianId)> AssignedVisitAsync(HttpClient client, DateTimeOffset day)
    {
        var technician = await CreateTechnicianAsync(client);
        var work = await ExistingWorkAsync(client, technician, day.AddHours(9), day.AddHours(10));
        return (work.Visit.Id, technician);
    }

    private static async Task<HttpResponseMessage> StepAsync(HttpClient client, Guid visitId, string step) =>
        await client.PostAsync($"/api/visits/{visitId}/execution/{step}", null, Cancellation);

    private static async Task<VisitExecutionResponse> OkStepAsync(HttpClient client, Guid visitId, string step)
    {
        using var response = await StepAsync(client, visitId, step);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<VisitExecutionResponse>(Cancellation))!;
    }

    [Fact]
    public async Task The_full_field_work_flow_returns_200_for_each_step_and_GET_shows_the_completed_execution()
    {
        using var client = factory.CreateClient();
        var (visitId, _) = await AssignedVisitAsync(client, NewWorkDay());

        var notStarted = await GetAsync<VisitExecutionResponse>(client, $"/api/visits/{visitId}/execution");
        Assert.Equal(("NotStarted", (Guid?)null), (notStarted.Status, notStarted.ExecutionId));

        var traveling = await OkStepAsync(client, visitId, "start-travel");
        var working = await OkStepAsync(client, visitId, "start-work");
        var paused = await OkStepAsync(client, visitId, "pause");
        var resumed = await OkStepAsync(client, visitId, "resume");
        var completed = await OkStepAsync(client, visitId, "complete");

        Assert.Equal(["Traveling", "Working", "Paused", "Working", "Completed"],
            new[] { traveling, working, paused, resumed, completed }.Select(r => r.Status));
        Assert.NotNull(traveling.ExecutionId);
        Assert.All(new[] { working, paused, resumed, completed }, r => Assert.Equal(traveling.ExecutionId, r.ExecutionId));
        Assert.Null(Assert.Single(paused.Pauses).EndedAtUtc);
        Assert.NotNull(Assert.Single(resumed.Pauses).EndedAtUtc);

        var execution = await GetAsync<VisitExecutionResponse>(client, $"/api/visits/{visitId}/execution");
        AssertSameJson(completed, execution);
        Assert.True(execution.TravelStartedAtUtc <= execution.WorkStartedAtUtc && execution.WorkStartedAtUtc <= execution.CompletedAtUtc);
        Assert.Null(execution.CancelledAtUtc);
        var metrics = execution.Metrics;
        Assert.True(metrics.IsFinal);
        Assert.NotNull(metrics.TravelMinutes);
        Assert.Equal(metrics.GrossWorkMinutes!.Value - metrics.PauseMinutes, metrics.NetWorkMinutes!.Value, 2);

        // The plan is kept; the visit followed through its own lifecycle.
        var visit = await GetAsync<VisitResponse>(client, $"/api/visits/{visitId}");
        Assert.Equal("Completed", visit.Status);

        // Every step's event, in order, on the visit's history.
        var events = (await EventsAsync(client, "visit", visitId)).Select(e => e.EventType).Where(type => type != "VisitStatusChanged");
        Assert.Equal(
            ["VisitCreated", "VisitTravelStarted", "VisitWorkStarted", "VisitWorkPaused", "VisitWorkResumed", "VisitWorkCompleted"],
            events);
    }

    [Fact]
    public async Task Cancel_records_the_cancellation_and_keeps_the_assignment()
    {
        using var client = factory.CreateClient();
        var (visitId, _) = await AssignedVisitAsync(client, NewWorkDay());
        await OkStepAsync(client, visitId, "start-work");
        await OkStepAsync(client, visitId, "pause");

        var cancelled = await OkStepAsync(client, visitId, "cancel");

        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal(cancelled.CancelledAtUtc, Assert.Single(cancelled.Pauses).EndedAtUtc);
        Assert.False(cancelled.Metrics.IsFinal);
        Assert.Equal("Active", (await GetAsync<Contracts.Scheduling.AssignmentResponse>(client, $"/api/visits/{visitId}/assignment")).Status);
        Assert.Equal("VisitExecutionCancelled", (await EventsAsync(client, "visit", visitId)).Last().EventType);
    }

    [Fact]
    public async Task Invalid_steps_return_409_missing_assignment_409_and_missing_visit_404()
    {
        using var client = factory.CreateClient();
        var (visitId, _) = await AssignedVisitAsync(client, NewWorkDay());
        var workOrder = await ApiTestData.CreateWorkOrderAsync(client);
        var unassigned = await PostAsync<VisitResponse>(client, $"/api/work-orders/{workOrder.Id}/visits",
            new CreateVisitRequest(NewWorkDay().AddHours(9), NewWorkDay().AddHours(10), null));

        using (var pauseFirst = await StepAsync(client, visitId, "pause"))
        {
            Assert.Equal(HttpStatusCode.Conflict, pauseFirst.StatusCode);
            Assert.Equal("Field work transition not allowed", (await pauseFirst.Content.ReadFromJsonAsync<ProblemDetails>(Cancellation))!.Title);
        }

        await OkStepAsync(client, visitId, "start-work");
        await OkStepAsync(client, visitId, "complete");
        foreach (var step in new[] { "start-travel", "start-work", "pause", "resume", "complete", "cancel" })
        {
            using var response = await StepAsync(client, visitId, step);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        using (var noAssignment = await StepAsync(client, unassigned.Id, "start-travel"))
        {
            Assert.Equal(HttpStatusCode.Conflict, noAssignment.StatusCode);
            Assert.Equal("No active assignment", (await noAssignment.Content.ReadFromJsonAsync<ProblemDetails>(Cancellation))!.Title);
        }

        using (var noAssignmentWork = await StepAsync(client, unassigned.Id, "start-work"))
        {
            Assert.Equal(HttpStatusCode.Conflict, noAssignmentWork.StatusCode);
        }

        using var missingGet = await client.GetAsync($"/api/visits/{Guid.NewGuid()}/execution", Cancellation);
        using var missingStep = await StepAsync(client, Guid.NewGuid(), "start-work");
        Assert.Equal(HttpStatusCode.NotFound, missingGet.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingStep.StatusCode);
    }

    [Fact]
    public async Task The_calendar_shows_field_work_status_and_actual_metrics_and_keeps_visits_without_execution()
    {
        using var client = factory.CreateClient();
        var day = NewWorkDay();
        var technician = await CreateTechnicianAsync(client);
        async Task<Guid> VisitAsync(int hour) => (await ExistingWorkAsync(client, technician, day.AddHours(hour), day.AddHours(hour + 1))).Visit.Id;
        var untouched = await VisitAsync(6);
        var traveling = await VisitAsync(8);
        var working = await VisitAsync(10);
        var paused = await VisitAsync(12);
        var completed = await VisitAsync(14);
        await OkStepAsync(client, traveling, "start-travel");
        await OkStepAsync(client, working, "start-work");
        await OkStepAsync(client, paused, "start-work");
        await OkStepAsync(client, paused, "pause");
        await OkStepAsync(client, completed, "start-travel");
        await OkStepAsync(client, completed, "start-work");
        await OkStepAsync(client, completed, "pause");
        await OkStepAsync(client, completed, "resume");
        var done = await OkStepAsync(client, completed, "complete");

        var url = $"/api/operational-calendar?start={Uri.EscapeDataString(day.ToString("O"))}&end={Uri.EscapeDataString(day.AddDays(1).ToString("O"))}"
            + $"&timeZoneId=UTC&perspective=technician&perspectiveId={technician}";
        var calendar = await GetAsync<OperationalCalendarResponse>(client, url);
        var items = calendar.Items.ToDictionary(item => item.VisitId);

        Assert.Equal(5, calendar.TotalCount);
        Assert.Equal(
            ["NotStarted", "Traveling", "Working", "Paused", "Completed"],
            new[] { untouched, traveling, working, paused, completed }.Select(id => items[id].FieldWorkStatus));
        Assert.Equal((null, null, null, null), (items[untouched].TravelStartedAtUtc, items[untouched].WorkStartedAtUtc, items[untouched].PauseMinutes, items[untouched].ActualWorkMinutes));
        Assert.NotNull(items[traveling].TravelStartedAtUtc);
        Assert.Null(items[traveling].WorkStartedAtUtc);
        Assert.Null(items[working].ActualWorkMinutes); // not final yet
        Assert.Equal(0m, items[paused].PauseMinutes); // the open pause has no length yet

        var finished = items[completed];
        Assert.Equal((done.TravelStartedAtUtc, done.WorkStartedAtUtc, done.CompletedAtUtc),
            (finished.TravelStartedAtUtc, finished.WorkStartedAtUtc, finished.CompletedAtUtc));
        Assert.Equal((done.Metrics.TravelMinutes, done.Metrics.NetWorkMinutes, (decimal?)done.Metrics.PauseMinutes),
            (finished.ActualTravelMinutes, finished.ActualWorkMinutes, finished.PauseMinutes));
        Assert.NotNull(finished.ActualWorkMinutes);

        // The planned times are unchanged by execution.
        Assert.Equal((day.AddHours(14), day.AddHours(15)), (finished.VisitStartUtc, finished.VisitEndUtc));
    }

    [Theory]
    [InlineData("start-work", "start-work")]
    [InlineData("pause", "pause")]
    [InlineData("complete", "pause")]
    public async Task Concurrent_steps_give_one_200_one_409_and_one_consistent_state(string first, string second)
    {
        var clock = new HttpBarrierClock();
        await using var gated = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock)));
        using var client = gated.CreateClient();
        var (visitId, _) = await AssignedVisitAsync(client, NewWorkDay());
        if (first != "start-work")
        {
            await OkStepAsync(client, visitId, "start-work");
        }

        clock.Arm(2); // both load the same state before either saves
        var responses = await Task.WhenAll(StepAsync(client, visitId, first), StepAsync(client, visitId, second));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        var loser = (await responses.Single(r => r.StatusCode == HttpStatusCode.Conflict).Content.ReadFromJsonAsync<ProblemDetails>(Cancellation))!;
        Assert.Equal("Concurrent change", loser.Title);
        var winner = (await responses.Single(r => r.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<VisitExecutionResponse>(Cancellation))!;

        var execution = await GetAsync<VisitExecutionResponse>(client, $"/api/visits/{visitId}/execution");
        Assert.Equal(winner.Status, execution.Status);
        Assert.Equal(execution.Status == "Paused" ? 1 : 0, execution.Pauses.Length);
        Assert.Equal(1, await ExecutionCountAsync(visitId));

        var stepEvents = (await EventsAsync(client, "visit", visitId))
            .Count(e => e.EventType is "VisitWorkStarted" or "VisitWorkPaused" or "VisitWorkCompleted");
        Assert.Equal(first == "start-work" ? 1 : 2, stepEvents); // the setup's start-work plus only the winner's event

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    private async Task<int> ExecutionCountAsync(Guid visitId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().VisitExecutions.CountAsync(e => e.VisitId == visitId, Cancellation);
    }
}

/// <summary>
/// The system clock, except that once armed its next <c>callers</c> readers wait for each other: concurrent field work
/// requests then all have loaded the same state before any of them saves.
/// </summary>
internal sealed class HttpBarrierClock : TimeProvider
{
    private ManualResetEventSlim? _open;
    private int _remaining;

    public void Arm(int callers)
    {
        _remaining = callers;
        _open = new ManualResetEventSlim(false);
    }

    public override DateTimeOffset GetUtcNow()
    {
        if (_open is { IsSet: false } open)
        {
            if (Interlocked.Decrement(ref _remaining) <= 0)
            {
                open.Set();
            }
            else if (!open.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("The other request never read the clock.");
            }
        }

        return System.GetUtcNow();
    }
}
