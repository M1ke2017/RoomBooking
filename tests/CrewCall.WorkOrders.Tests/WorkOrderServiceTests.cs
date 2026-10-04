using System.Text.Json;
using CrewCall.Persistence.Operations;
using CrewCall.WorkOrders.Operations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CrewCall.WorkOrders.Tests.WorkOrdersTestData;

namespace CrewCall.WorkOrders.Tests;

public sealed class WorkOrderServiceTests(WorkOrdersDatabase database)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_saves_an_open_work_order_with_normal_priority_by_default()
    {
        await using var scope = database.CreateScope();
        var customerId = await CreateCustomerAsync(scope);
        var siteId = await CreateSiteAsync(scope, customerId);
        var workOrders = scope.ServiceProvider.GetRequiredService<WorkOrderService>();

        var outcome = await workOrders.CreateAsync(new CreateWorkOrder(customerId, siteId, "  Replace inverter  ", " Unit 3 ", null), Cancellation);

        var created = Assert.IsType<CreateWorkOrderOutcome.Created>(outcome).WorkOrder;
        Assert.Equal("Replace inverter", created.Title);
        Assert.Equal("Unit 3", created.Description);
        Assert.Equal(WorkOrderPriority.Normal, created.Priority);
        Assert.Equal(WorkOrderStatus.Open, created.Status);
        Assert.Equal(created.Id, (await workOrders.GetAsync(created.Id, Cancellation))?.Id);
    }

    [Fact]
    public async Task Create_accepts_urgent_as_a_priority()
    {
        await using var scope = database.CreateScope();

        var workOrder = await CreateWorkOrderAsync(scope, priority: "urgent");

        Assert.Equal(WorkOrderPriority.Urgent, workOrder.Priority);
        Assert.Equal(WorkOrderStatus.Open, workOrder.Status);
    }

    [Fact]
    public async Task Create_rejects_a_missing_customer()
    {
        await using var scope = database.CreateScope();
        var siteId = await CreateSiteAsync(scope, await CreateCustomerAsync(scope));
        var workOrders = scope.ServiceProvider.GetRequiredService<WorkOrderService>();

        var outcome = await workOrders.CreateAsync(new CreateWorkOrder(Guid.NewGuid(), siteId, "Title", null, null), Cancellation);

        Assert.IsType<CreateWorkOrderOutcome.CustomerNotFound>(outcome);
    }

    [Fact]
    public async Task Create_rejects_a_missing_site()
    {
        await using var scope = database.CreateScope();
        var customerId = await CreateCustomerAsync(scope);
        var workOrders = scope.ServiceProvider.GetRequiredService<WorkOrderService>();

        var outcome = await workOrders.CreateAsync(new CreateWorkOrder(customerId, Guid.NewGuid(), "Title", null, null), Cancellation);

        Assert.IsType<CreateWorkOrderOutcome.SiteNotFound>(outcome);
    }

    [Fact]
    public async Task Create_rejects_a_site_that_belongs_to_another_customer()
    {
        await using var scope = database.CreateScope();
        var customerA = await CreateCustomerAsync(scope);
        var siteOfCustomerB = await CreateSiteAsync(scope, await CreateCustomerAsync(scope));
        var workOrders = scope.ServiceProvider.GetRequiredService<WorkOrderService>();

        var outcome = await workOrders.CreateAsync(new CreateWorkOrder(customerA, siteOfCustomerB, "Title", null, null), Cancellation);

        Assert.Contains("siteId", Assert.IsType<CreateWorkOrderOutcome.Invalid>(outcome).Errors.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Create_rejects_an_empty_title(string? title)
    {
        await using var scope = database.CreateScope();
        var customerId = await CreateCustomerAsync(scope);
        var siteId = await CreateSiteAsync(scope, customerId);
        var workOrders = scope.ServiceProvider.GetRequiredService<WorkOrderService>();

        var outcome = await workOrders.CreateAsync(new CreateWorkOrder(customerId, siteId, title, null, null), Cancellation);

        Assert.Contains("title", Assert.IsType<CreateWorkOrderOutcome.Invalid>(outcome).Errors.Keys);
    }

    [Theory]
    [InlineData("Critical")]
    [InlineData("2")]
    public async Task Create_rejects_an_unknown_priority(string priority)
    {
        await using var scope = database.CreateScope();
        var customerId = await CreateCustomerAsync(scope);
        var siteId = await CreateSiteAsync(scope, customerId);
        var workOrders = scope.ServiceProvider.GetRequiredService<WorkOrderService>();

        var outcome = await workOrders.CreateAsync(new CreateWorkOrder(customerId, siteId, "Title", null, priority), Cancellation);

        Assert.Contains("priority", Assert.IsType<CreateWorkOrderOutcome.Invalid>(outcome).Errors.Keys);
    }

    [Fact]
    public async Task Create_writes_a_WorkOrderCreated_event_with_a_minimal_payload()
    {
        await using var scope = database.CreateScope();

        var workOrder = await CreateWorkOrderAsync(scope, priority: "High");

        var single = Assert.Single(await EventsAsync(scope, workOrder.Id));
        Assert.Equal(WorkOrderEvents.WorkOrderCreated, single.EventType);
        Assert.Equal(WorkOrderEvents.WorkOrderAggregate, single.AggregateType);
        using var payload = JsonDocument.Parse(single.PayloadJson);
        Assert.Equal(workOrder.Id, payload.RootElement.GetProperty("workOrderId").GetGuid());
        Assert.Equal(workOrder.CustomerId, payload.RootElement.GetProperty("customerId").GetGuid());
        Assert.Equal(workOrder.SiteId, payload.RootElement.GetProperty("siteId").GetGuid());
        Assert.Equal("High", payload.RootElement.GetProperty("priority").GetString());
        Assert.False(payload.RootElement.TryGetProperty("title", out _), "The payload must not carry the whole entity.");
    }

    [Fact]
    public async Task Status_moves_through_Open_Planned_InProgress_Completed_and_records_each_change()
    {
        await using var scope = database.CreateScope();
        var workOrder = await CreateWorkOrderAsync(scope);
        var workOrders = scope.ServiceProvider.GetRequiredService<WorkOrderService>();

        foreach (var (from, to) in new[] { ("Open", "Planned"), ("Planned", "InProgress"), ("InProgress", "Completed") })
        {
            var outcome = await workOrders.ChangeStatusAsync(new ChangeWorkOrderStatus(workOrder.Id, to), Cancellation);
            var changed = Assert.IsType<ChangeWorkOrderStatusOutcome.Changed>(outcome);
            Assert.Equal(from, changed.OldStatus.ToString());
            Assert.Equal(to, changed.WorkOrder.Status.ToString());
        }

        var events = await EventsAsync(scope, workOrder.Id);
        Assert.Equal(
            [WorkOrderEvents.WorkOrderCreated, WorkOrderEvents.WorkOrderStatusChanged, WorkOrderEvents.WorkOrderStatusChanged, WorkOrderEvents.WorkOrderStatusChanged],
            events.Select(e => e.EventType));
        using var last = JsonDocument.Parse(events[^1].PayloadJson);
        Assert.Equal("InProgress", last.RootElement.GetProperty("oldStatus").GetString());
        Assert.Equal("Completed", last.RootElement.GetProperty("newStatus").GetString());
    }

    [Fact]
    public async Task Status_cannot_return_from_Completed_to_InProgress_and_records_nothing()
    {
        await using var scope = database.CreateScope();
        var workOrder = await CreateWorkOrderAsync(scope);
        var workOrders = scope.ServiceProvider.GetRequiredService<WorkOrderService>();
        foreach (var status in new[] { "Planned", "InProgress", "Completed" })
        {
            Assert.IsType<ChangeWorkOrderStatusOutcome.Changed>(
                await workOrders.ChangeStatusAsync(new ChangeWorkOrderStatus(workOrder.Id, status), Cancellation));
        }

        var outcome = await workOrders.ChangeStatusAsync(new ChangeWorkOrderStatus(workOrder.Id, "InProgress"), Cancellation);

        var notAllowed = Assert.IsType<ChangeWorkOrderStatusOutcome.TransitionNotAllowed>(outcome);
        Assert.Equal(WorkOrderStatus.Completed, notAllowed.From);
        Assert.Equal(4, (await EventsAsync(scope, workOrder.Id)).Count);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Open, WorkOrderStatus.InProgress)]
    [InlineData(WorkOrderStatus.Open, WorkOrderStatus.Completed)]
    [InlineData(WorkOrderStatus.Planned, WorkOrderStatus.Open)]
    [InlineData(WorkOrderStatus.Cancelled, WorkOrderStatus.InProgress)]
    [InlineData(WorkOrderStatus.Completed, WorkOrderStatus.Cancelled)]
    public void Lifecycle_rejects_transitions_outside_the_allowed_set(WorkOrderStatus from, WorkOrderStatus to) =>
        Assert.False(WorkOrderLifecycle.CanTransition(from, to));

    [Fact]
    public async Task ChangeStatus_rejects_a_missing_work_order_and_an_unknown_status()
    {
        await using var scope = database.CreateScope();
        var workOrder = await CreateWorkOrderAsync(scope);
        var workOrders = scope.ServiceProvider.GetRequiredService<WorkOrderService>();

        Assert.IsType<ChangeWorkOrderStatusOutcome.NotFound>(
            await workOrders.ChangeStatusAsync(new ChangeWorkOrderStatus(Guid.NewGuid(), "Planned"), Cancellation));
        Assert.IsType<ChangeWorkOrderStatusOutcome.Invalid>(
            await workOrders.ChangeStatusAsync(new ChangeWorkOrderStatus(workOrder.Id, "Done"), Cancellation));
    }

    private static Task<IReadOnlyList<OperationalEvent>> EventsAsync(AsyncServiceScope scope, Guid workOrderId) =>
        scope.ServiceProvider.GetRequiredService<OperationalEventLog>()
            .ListForAggregateAsync(WorkOrderEvents.WorkOrderAggregate, workOrderId, Cancellation);
}
