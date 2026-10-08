extern alias reporting;

using CrewCall.Contracts.Reporting;
using Microsoft.EntityFrameworkCore;
using reporting::CrewCall.Reporting.Api;
using reporting::CrewCall.Reporting.Data;
using reporting::CrewCall.Reporting.Projections;
using Xunit;
using static CrewCall.Reporting.Tests.Events;

namespace CrewCall.Reporting.Tests;

/// <summary>The projections and the reporting inbox against the real reporting database (ADR-0016).</summary>
[Collection(Sequential.Name)]
public sealed class ProjectionTests(ReportingInfrastructure infrastructure) : IAsyncLifetime
{
    private readonly ManualClock _clock = new(Day1.AddDays(30));
    private ProjectionHarness _harness = null!;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await infrastructure.ResetAsync();
        _harness = new ProjectionHarness(infrastructure, _clock);
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    // ---- inbox / idempotency ----

    [Fact]
    public async Task The_first_delivery_is_projected_and_a_duplicate_is_ignored_without_counting_twice()
    {
        var (visitId, technicianId) = (Guid.NewGuid(), Guid.NewGuid());
        await _harness.ProcessAsync(AssignmentCreated(Guid.NewGuid(), visitId, technicianId, Day1.AddHours(8)));
        var completion = Envelope(WorkCompleted(visitId, Day1.AddHours(11), 20m, 90m, 10m));

        Assert.Equal(ProjectionResult.Applied, await _harness.ProcessAsync(completion));
        Assert.Equal(ProjectionResult.Duplicate, await _harness.ProcessAsync(completion));
        Assert.Equal(ProjectionResult.Duplicate, await _harness.ProcessAsync(completion));

        var day = Assert.Single(await _harness.DaysAsync(technicianId));
        Assert.Equal((1, 1, 20m, 90m, 10m, 80m), (day.CompletedVisits, day.AssignmentCount, day.TravelMinutes, day.GrossWorkMinutes, day.PauseMinutes, day.NetWorkMinutes));
        Assert.Equal(1, await _harness.InboxCountAsync(completion.MessageId));
        var inbox = await _harness.ReadAsync(db => db.InboxMessages.AsNoTracking().SingleAsync(m => m.MessageId == completion.MessageId, Cancellation));
        Assert.Equal(("crewcall-reporting", "visit.work-completed", 1), (inbox.ConsumerName, inbox.Type, inbox.Version));
        Assert.NotNull(inbox.ProcessedAtUtc);
    }

    [Fact]
    public async Task A_failure_before_the_commit_leaves_neither_inbox_record_nor_projection_and_the_retry_succeeds()
    {
        var visitId = Guid.NewGuid();
        var created = Envelope(VisitCreated(visitId, Day1.AddHours(9), TimeSpan.FromHours(1)));
        await using (await FailVisitWritesAsync(visitId))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => _harness.ProcessAsync(created));
            Assert.Equal(0, await _harness.InboxCountAsync(created.MessageId)); // the inbox record rolled back with the projection
            Assert.Null(await _harness.VisitAsync(visitId));
        }

        Assert.Equal(ProjectionResult.Applied, await _harness.ProcessAsync(created));
        Assert.Equal(1, await _harness.InboxCountAsync(created.MessageId));
        Assert.Equal("Planned", (await _harness.VisitAsync(visitId))!.VisitStatus);
    }

    [Fact]
    public async Task Unsupported_versions_are_ignored_and_poison_payloads_rejected_without_a_trace()
    {
        var created = Envelope(VisitCreated(Guid.NewGuid(), Day1, TimeSpan.FromHours(1)));

        Assert.Equal(ProjectionResult.Unsupported, await _harness.ProcessAsync(created with { Version = 2 }));
        await Assert.ThrowsAsync<CrewCall.Messaging.PoisonMessageException>(() => _harness.ProcessAsync(created with { MessageId = Guid.NewGuid() }));
        Assert.Equal("[]", await _harness.ReadAsync(async db => System.Text.Json.JsonSerializer.Serialize(await db.InboxMessages.ToListAsync(Cancellation))));
    }

    // ---- assignments ----

    [Fact]
    public async Task Assignment_created_projects_an_active_assignment_and_counts_it_on_its_day()
    {
        var (assignmentId, visitId, technicianId, vehicleId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var created = AssignmentCreated(assignmentId, visitId, technicianId, Day1.AddHours(7)) with { VehicleId = vehicleId };

        await _harness.ProcessAsync(created);
        await _harness.ProcessAsync(created); // the same message again

        var assignment = Assert.Single(await _harness.AssignmentsAsync(visitId));
        Assert.Equal((assignmentId, (Guid?)technicianId, (Guid?)vehicleId, "Active", (DateTimeOffset?)Day1.AddHours(7), (DateTimeOffset?)null),
            (assignment.AssignmentId, assignment.TechnicianId, assignment.VehicleId, assignment.Status, assignment.CreatedAtUtc, assignment.EndedAtUtc));
        Assert.Equal(1, Assert.Single(await _harness.DaysAsync(technicianId)).AssignmentCount);
    }

    [Fact]
    public async Task A_reassignment_keeps_the_history_the_old_assignment_replaced_and_the_new_one_active()
    {
        var (visitId, first, second, technicianA, technicianB) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _harness.ProcessAsync(AssignmentCreated(first, visitId, technicianA, Day1.AddHours(7)));
        await _harness.ProcessAsync(AssignmentCreated(second, visitId, technicianB, Day1.AddHours(8)));
        await _harness.ProcessAsync(AssignmentReplaced(first, second, visitId, Day1.AddHours(8)));

        var history = await _harness.AssignmentsAsync(visitId);
        Assert.Equal([(first, technicianA, "Replaced", (Guid?)second), (second, technicianB, "Active", null)],
            history.Select(a => (a.AssignmentId, a.TechnicianId!.Value, a.Status, a.ReplacedByAssignmentId)));
        Assert.Equal(Day1.AddHours(8), history[0].EndedAtUtc);
        // Both technicians were given the visit that day: one assignment each, nobody counted twice.
        Assert.Equal(1, Assert.Single(await _harness.DaysAsync(technicianA)).AssignmentCount);
        Assert.Equal(1, Assert.Single(await _harness.DaysAsync(technicianB)).AssignmentCount);
    }

    [Fact]
    public async Task Assignment_cancelled_ends_the_assignment_and_keeps_it_in_the_history()
    {
        var (assignmentId, visitId, technicianId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _harness.ProcessAsync(AssignmentCreated(assignmentId, visitId, technicianId, Day1.AddHours(7)));
        await _harness.ProcessAsync(AssignmentCancelled(assignmentId, visitId, Day1.AddHours(9)));

        var assignment = Assert.Single(await _harness.AssignmentsAsync(visitId));
        Assert.Equal(("Cancelled", (DateTimeOffset?)Day1.AddHours(9)), (assignment.Status, assignment.EndedAtUtc));
        Assert.Equal(1, Assert.Single(await _harness.DaysAsync(technicianId)).AssignmentCount);
    }

    // ---- visit completion ----

    [Fact]
    public async Task A_completed_visit_stores_its_actual_durations_and_updates_the_technicians_day_once()
    {
        var (visitId, technicianId) = (Guid.NewGuid(), Guid.NewGuid());
        await _harness.ProcessAsync(VisitCreated(visitId, Day1.AddHours(9), TimeSpan.FromHours(1)));
        await _harness.ProcessAsync(AssignmentCreated(Guid.NewGuid(), visitId, technicianId, Day1.AddDays(-1)));
        var completion = Envelope(WorkCompleted(visitId, Day1.AddHours(10).AddMinutes(30), 25.5m, 100m, 12.25m));
        await _harness.ProcessAsync(completion);
        await _harness.ProcessAsync(completion);

        var visit = (await _harness.VisitAsync(visitId))!;
        Assert.Equal((25.5m, 100m, 12.25m, 87.75m, "Completed", (DateTimeOffset?)Day1.AddHours(10).AddMinutes(30), (Guid?)technicianId),
            (visit.ActualTravelMinutes, visit.ActualGrossWorkMinutes, visit.ActualPauseMinutes, visit.ActualNetWorkMinutes, visit.VisitStatus, visit.CompletedAtUtc, visit.TechnicianId));

        var days = await _harness.DaysAsync(technicianId);
        Assert.Equal([Day1Date.AddDays(-1), Day1Date], days.Select(d => d.DateUtc));
        var completedDay = days[1];
        Assert.Equal((1, 0, 25.5m, 100m, 12.25m, 87.75m),
            (completedDay.CompletedVisits, completedDay.AssignmentCount, completedDay.TravelMinutes, completedDay.GrossWorkMinutes, completedDay.PauseMinutes, completedDay.NetWorkMinutes));
    }

    [Fact]
    public async Task Visit_statuses_follow_business_time_not_arrival_order()
    {
        var visitId = Guid.NewGuid();
        await _harness.ProcessAsync(StatusChanged(visitId, "InProgress", "Completed", Day1.AddHours(11)));
        await _harness.ProcessAsync(StatusChanged(visitId, "Planned", "InProgress", Day1.AddHours(9)));
        await _harness.ProcessAsync(VisitCreated(visitId, Day1.AddHours(9), TimeSpan.FromHours(2), at: Day1.AddHours(-3)));

        var visit = (await _harness.VisitAsync(visitId))!;
        Assert.Equal(("Completed", (DateTimeOffset?)Day1.AddHours(9)), (visit.VisitStatus, visit.PlannedStartUtc));
    }

    // ---- incidents ----

    [Fact]
    public async Task An_incident_dispatch_is_projected_and_counted_once_even_with_its_assignment_created_and_a_duplicate()
    {
        var (incidentId, visitId, assignmentId, technicianId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var dispatch = Envelope(IncidentDispatched(incidentId, visitId, assignmentId, technicianId, Day1.AddHours(13)));
        await _harness.ProcessAsync(dispatch);
        await _harness.ProcessAsync(dispatch);
        await _harness.ProcessAsync(AssignmentCreated(assignmentId, visitId, technicianId, Day1.AddHours(13)));

        var incident = await _harness.ReadAsync(db => db.IncidentActivities.AsNoTracking().SingleAsync(i => i.IncidentId == incidentId, Cancellation));
        Assert.Equal((visitId, assignmentId, technicianId, Day1.AddHours(13)), (incident.VisitId, incident.AssignmentId, incident.TechnicianId, incident.DispatchedAtUtc));
        var day = Assert.Single(await _harness.DaysAsync(technicianId));
        Assert.Equal((1, 1), (day.IncidentDispatchCount, day.AssignmentCount));
    }

    // ---- planned vs actual, utilization ----

    [Fact]
    public async Task Planned_versus_actual_gives_the_variance_only_when_an_actual_exists()
    {
        var (done, open, technicianId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        foreach (var visitId in new[] { done, open })
        {
            await _harness.ProcessAsync(VisitCreated(visitId, Day1.AddHours(9), TimeSpan.FromHours(1)));
            await _harness.ProcessAsync(AssignmentCreated(Guid.NewGuid(), visitId, technicianId, Day1.AddHours(7)));
        }

        // Planned 09:00–10:00 = 60 min; actual net work 75 min.
        await _harness.ProcessAsync(WorkCompleted(done, Day1.AddHours(10).AddMinutes(30), 10m, 80m, 5m));

        var report = await _harness.QueryAsync(q => q.VisitsAsync(new VisitReportFilter(technicianId, null, null, null, null), Cancellation));
        var completed = Assert.Single(report.Visits, v => v.VisitId == done);
        Assert.Equal((60m, 75m, 15m), (completed.PlannedDurationMinutes, completed.ActualNetWorkMinutes, completed.VarianceMinutes));
        var planned = Assert.Single(report.Visits, v => v.VisitId == open);
        Assert.Equal((60m, (decimal?)null, (decimal?)null, "Planned"), (planned.PlannedDurationMinutes, planned.ActualNetWorkMinutes, planned.VarianceMinutes, planned.Status));
    }

    [Fact]
    public void Operational_utilization_is_net_over_gross_plus_travel_and_null_without_time()
    {
        TechnicianActivityDay Day(decimal travel, decimal gross, decimal pause) => new(Day1Date, 1, 0, 0, travel, gross, pause, gross - pause);

        Assert.Equal(0.75m, ReportingQueries.OperationalUtilization([Day(20m, 80m, 5m)])); // 75 / (80 + 20)
        Assert.Equal(0.9m, ReportingQueries.OperationalUtilization([Day(0m, 50m, 5m), Day(0m, 50m, 5m)]));
        Assert.Null(ReportingQueries.OperationalUtilization([]));
        Assert.Null(ReportingQueries.OperationalUtilization([Day(0m, 0m, 0m)]));
    }

    // ---- rescheduling ----

    [Fact]
    public async Task A_reschedule_moves_the_planned_window_counts_once_and_adds_its_delay_even_when_redelivered()
    {
        var (visitId, technicianId, incidentId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _harness.ProcessAsync(VisitCreated(visitId, Day1.AddHours(10.5), TimeSpan.FromHours(1.5)));
        await _harness.ProcessAsync(AssignmentCreated(Guid.NewGuid(), visitId, technicianId, Day1.AddHours(7)));
        var moved = Envelope(Rescheduled(visitId, Day1.AddHours(10.5), Day1.AddHours(14), TimeSpan.FromHours(1.5), Day1.AddHours(9), incidentId));

        Assert.Equal(ProjectionResult.Applied, await _harness.ProcessAsync(moved));
        Assert.Equal(ProjectionResult.Duplicate, await _harness.ProcessAsync(moved));

        var visit = (await _harness.VisitAsync(visitId))!;
        Assert.Equal((1, 210, Day1.AddHours(14), Day1.AddHours(15.5), "Planned"),
            (visit.RescheduleCount, visit.TotalDelayMinutes, visit.PlannedStartUtc!.Value, visit.PlannedEndUtc!.Value, visit.VisitStatus));

        // The visit report shows the current plan and the reschedule effect.
        var item = Assert.Single((await _harness.QueryAsync(q => q.VisitsAsync(new VisitReportFilter(technicianId, null, null, null, null), Cancellation))).Visits);
        Assert.Equal((Day1.AddHours(14), 90m, 1, 210), (item.PlannedStartUtc!.Value, item.PlannedDurationMinutes!.Value, item.RescheduleCount, item.TotalDelayMinutes));
    }

    [Fact]
    public async Task Reschedules_give_the_same_rows_in_any_order_and_a_late_visit_created_never_overwrites_a_newer_plan()
    {
        var visitId = Guid.NewGuid();
        var created = Envelope(VisitCreated(visitId, Day1.AddHours(9), TimeSpan.FromHours(1), at: Day1.AddHours(-24)));
        var first = Envelope(Rescheduled(visitId, Day1.AddHours(9), Day1.AddHours(11), TimeSpan.FromHours(1), Day1.AddHours(7)));
        var second = Envelope(Rescheduled(visitId, Day1.AddHours(11), Day1.AddHours(13.5), TimeSpan.FromHours(1), Day1.AddHours(8)));

        await _harness.ProcessAllAsync([created, first, second]);
        var inOrder = await _harness.VisitAsync(visitId);
        await _harness.ResetAsync();
        await _harness.ProcessAllAsync([second, first, created]);
        var reversed = await _harness.VisitAsync(visitId);

        foreach (var visit in new[] { inOrder!, reversed! })
        {
            Assert.Equal((2, 270, Day1.AddHours(13.5), Day1.AddHours(14.5), Day1.AddHours(8)),
                (visit.RescheduleCount, visit.TotalDelayMinutes, visit.PlannedStartUtc!.Value, visit.PlannedEndUtc!.Value, visit.PlannedChangedAtUtc!.Value));
        }
    }

    private static DateOnly Day1Date => DateOnly.FromDateTime(Day1.UtcDateTime);

    /// <summary>Makes the reporting database reject this visit's row until disposed: a failure inside the projection.</summary>
    private async Task<IAsyncDisposable> FailVisitWritesAsync(Guid visitId)
    {
        var name = $"test_fail_visit_{visitId:N}";
        await ExecuteAsync($"""
            CREATE FUNCTION reporting.{name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'forced projection failure'; END $$;
            CREATE TRIGGER {name} BEFORE INSERT OR UPDATE ON reporting.visit_activity
            FOR EACH ROW WHEN (NEW.visit_id = '{visitId}') EXECUTE FUNCTION reporting.{name}();
            """);
        return new Cleanup(() => ExecuteAsync($"DROP TRIGGER {name} ON reporting.visit_activity; DROP FUNCTION reporting.{name}();"));
    }

    private Task ExecuteAsync(string sql) => _harness.ReadAsync(async db =>
    {
#pragma warning disable EF1002 // test-only DDL built from a Guid
        return await db.Database.ExecuteSqlRawAsync(sql, Cancellation);
#pragma warning restore EF1002
    });

    private sealed class Cleanup(Func<Task> cleanup) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await cleanup();
    }
}
