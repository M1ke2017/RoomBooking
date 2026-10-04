using System.Text.Json;
using CrewCall.Persistence.Operations;
using CrewCall.WorkOrders.Operations;
using CrewCall.WorkOrders.Visits;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CrewCall.WorkOrders.Tests.WorkOrdersTestData;

namespace CrewCall.WorkOrders.Tests;

public sealed class VisitServiceTests(WorkOrdersDatabase database)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Morning = new(2026, 10, 6, 8, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public async Task Create_saves_a_planned_visit_for_an_existing_work_order()
    {
        await using var scope = database.CreateScope();
        var workOrder = await CreateWorkOrderAsync(scope);
        var visits = scope.ServiceProvider.GetRequiredService<VisitService>();

        var outcome = await visits.CreateAsync(new CreateVisit(workOrder.Id, Morning, Morning.AddHours(2), " Bring ladder "), Cancellation);

        var visit = Assert.IsType<CreateVisitOutcome.Created>(outcome).Visit;
        Assert.Equal(VisitStatus.Planned, visit.Status);
        Assert.Equal("Bring ladder", visit.Notes);
        var saved = await visits.GetAsync(visit.Id, Cancellation);
        Assert.NotNull(saved);
        Assert.Equal(Morning, saved.Start);
        Assert.Equal(Morning.AddHours(2), saved.End);
        Assert.Equal(visit.Id, Assert.Single((await visits.ListForWorkOrderAsync(workOrder.Id, Cancellation))!).Id);
    }

    [Fact]
    public async Task Create_rejects_a_missing_work_order()
    {
        await using var scope = database.CreateScope();
        var visits = scope.ServiceProvider.GetRequiredService<VisitService>();

        var outcome = await visits.CreateAsync(new CreateVisit(Guid.NewGuid(), Morning, Morning.AddHours(1), null), Cancellation);

        Assert.IsType<CreateVisitOutcome.WorkOrderNotFound>(outcome);
    }

    [Fact]
    public async Task Create_rejects_a_visit_whose_start_equals_its_end()
    {
        await using var scope = database.CreateScope();
        var workOrder = await CreateWorkOrderAsync(scope);
        var visits = scope.ServiceProvider.GetRequiredService<VisitService>();

        var outcome = await visits.CreateAsync(new CreateVisit(workOrder.Id, Morning, Morning, null), Cancellation);

        Assert.Contains("end", Assert.IsType<CreateVisitOutcome.Invalid>(outcome).Errors.Keys);
    }

    [Fact]
    public async Task Create_rejects_a_visit_whose_start_is_after_its_end()
    {
        await using var scope = database.CreateScope();
        var workOrder = await CreateWorkOrderAsync(scope);
        var visits = scope.ServiceProvider.GetRequiredService<VisitService>();

        var outcome = await visits.CreateAsync(new CreateVisit(workOrder.Id, Morning.AddHours(2), Morning, null), Cancellation);

        Assert.Contains("end", Assert.IsType<CreateVisitOutcome.Invalid>(outcome).Errors.Keys);
    }

    [Fact]
    public async Task Create_rejects_times_that_differ_only_below_the_stored_microsecond_precision()
    {
        await using var scope = database.CreateScope();
        var workOrder = await CreateWorkOrderAsync(scope);
        var visits = scope.ServiceProvider.GetRequiredService<VisitService>();

        var outcome = await visits.CreateAsync(new CreateVisit(workOrder.Id, Morning, Morning.AddTicks(5), null), Cancellation);

        Assert.Contains("end", Assert.IsType<CreateVisitOutcome.Invalid>(outcome).Errors.Keys);
    }

    [Fact]
    public async Task Create_rejects_a_visit_for_a_closed_work_order()
    {
        await using var scope = database.CreateScope();
        var workOrder = await CreateWorkOrderAsync(scope);
        Assert.IsType<ChangeWorkOrderStatusOutcome.Changed>(await scope.ServiceProvider.GetRequiredService<WorkOrderService>()
            .ChangeStatusAsync(new ChangeWorkOrderStatus(workOrder.Id, "Cancelled"), Cancellation));
        var visits = scope.ServiceProvider.GetRequiredService<VisitService>();

        var outcome = await visits.CreateAsync(new CreateVisit(workOrder.Id, Morning, Morning.AddHours(1), null), Cancellation);

        Assert.Equal(WorkOrderStatus.Cancelled, Assert.IsType<CreateVisitOutcome.WorkOrderClosed>(outcome).Status);
    }

    [Fact]
    public async Task Create_writes_a_VisitCreated_event()
    {
        await using var scope = database.CreateScope();
        var workOrder = await CreateWorkOrderAsync(scope);

        var visit = await CreateVisitAsync(scope, workOrder.Id);

        var single = Assert.Single(await EventsAsync(scope, visit.Id));
        Assert.Equal(WorkOrderEvents.VisitCreated, single.EventType);
        using var payload = JsonDocument.Parse(single.PayloadJson);
        Assert.Equal(visit.Id, payload.RootElement.GetProperty("visitId").GetGuid());
        Assert.Equal(workOrder.Id, payload.RootElement.GetProperty("workOrderId").GetGuid());
        Assert.Equal(visit.Start, payload.RootElement.GetProperty("start").GetDateTimeOffset());
        Assert.Equal(visit.End, payload.RootElement.GetProperty("end").GetDateTimeOffset());
    }

    [Fact]
    public async Task Status_moves_Planned_InProgress_Completed_and_records_each_change()
    {
        await using var scope = database.CreateScope();
        var visit = await CreateVisitAsync(scope, (await CreateWorkOrderAsync(scope)).Id);
        var visits = scope.ServiceProvider.GetRequiredService<VisitService>();

        Assert.Equal(VisitStatus.InProgress, Assert.IsType<ChangeVisitStatusOutcome.Changed>(
            await visits.ChangeStatusAsync(new ChangeVisitStatus(visit.Id, "InProgress"), Cancellation)).Visit.Status);
        Assert.Equal(VisitStatus.Completed, Assert.IsType<ChangeVisitStatusOutcome.Changed>(
            await visits.ChangeStatusAsync(new ChangeVisitStatus(visit.Id, "Completed"), Cancellation)).Visit.Status);

        var events = await EventsAsync(scope, visit.Id);
        Assert.Equal(
            [WorkOrderEvents.VisitCreated, WorkOrderEvents.VisitStatusChanged, WorkOrderEvents.VisitStatusChanged],
            events.Select(e => e.EventType));
        using var last = JsonDocument.Parse(events[^1].PayloadJson);
        Assert.Equal("InProgress", last.RootElement.GetProperty("oldStatus").GetString());
        Assert.Equal("Completed", last.RootElement.GetProperty("newStatus").GetString());
    }

    [Fact]
    public async Task Status_cannot_return_from_Completed_to_InProgress()
    {
        await using var scope = database.CreateScope();
        var visit = await CreateVisitAsync(scope, (await CreateWorkOrderAsync(scope)).Id);
        var visits = scope.ServiceProvider.GetRequiredService<VisitService>();
        Assert.IsType<ChangeVisitStatusOutcome.Changed>(await visits.ChangeStatusAsync(new ChangeVisitStatus(visit.Id, "InProgress"), Cancellation));
        Assert.IsType<ChangeVisitStatusOutcome.Changed>(await visits.ChangeStatusAsync(new ChangeVisitStatus(visit.Id, "Completed"), Cancellation));

        var outcome = await visits.ChangeStatusAsync(new ChangeVisitStatus(visit.Id, "InProgress"), Cancellation);

        Assert.Equal(VisitStatus.Completed, Assert.IsType<ChangeVisitStatusOutcome.TransitionNotAllowed>(outcome).From);
        Assert.Equal(3, (await EventsAsync(scope, visit.Id)).Count);
    }

    private static Task<IReadOnlyList<OperationalEvent>> EventsAsync(AsyncServiceScope scope, Guid visitId) =>
        scope.ServiceProvider.GetRequiredService<OperationalEventLog>()
            .ListForAggregateAsync(WorkOrderEvents.VisitAggregate, visitId, Cancellation);
}
