using System.Net;
using System.Net.Http.Json;
using CrewCall.Contracts.Operations;
using CrewCall.Contracts.Visits;
using CrewCall.Contracts.WorkOrders;
using Xunit;

namespace CrewCall.Api.Tests;

public sealed class WorkOrderEndpointsTests(CrewCallApiFactory factory)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Morning = new(2026, 10, 6, 8, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public async Task Post_work_order_returns_201_and_get_returns_it()
    {
        using var client = factory.CreateClient();
        var customerId = await ApiTestData.CreateCustomerAsync(client);
        var siteId = await ApiTestData.CreateSiteAsync(client, customerId);

        using var response = await client.PostAsJsonAsync("/api/work-orders",
            new CreateWorkOrderRequest(customerId, siteId, "Replace inverter", "Unit 3", "urgent"), Cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<WorkOrderResponse>(Cancellation);
        Assert.Equal("Urgent", created!.Priority);
        Assert.Equal("Open", created.Status);
        Assert.Equal($"/api/work-orders/{created.Id}", response.Headers.Location?.OriginalString);

        var fetched = await client.GetFromJsonAsync<WorkOrderResponse>($"/api/work-orders/{created.Id}", Cancellation);
        Assert.Equal(created, fetched);
        Assert.Contains(await client.GetFromJsonAsync<WorkOrderResponse[]>("/api/work-orders", Cancellation) ?? [], w => w.Id == created.Id);
    }

    [Fact]
    public async Task Post_work_order_for_a_missing_customer_returns_404()
    {
        using var client = factory.CreateClient();
        var siteId = await ApiTestData.CreateSiteAsync(client, await ApiTestData.CreateCustomerAsync(client));

        using var response = await client.PostAsJsonAsync("/api/work-orders",
            new CreateWorkOrderRequest(Guid.NewGuid(), siteId, "Title", null, null), Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_work_order_with_a_site_of_another_customer_returns_400()
    {
        using var client = factory.CreateClient();
        var customerA = await ApiTestData.CreateCustomerAsync(client);
        var siteOfCustomerB = await ApiTestData.CreateSiteAsync(client, await ApiTestData.CreateCustomerAsync(client));

        using var response = await client.PostAsJsonAsync("/api/work-orders",
            new CreateWorkOrderRequest(customerA, siteOfCustomerB, "Title", null, null), Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_a_missing_work_order_returns_404()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/work-orders/{Guid.NewGuid()}", Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Status_transition_succeeds_and_an_invalid_one_returns_409()
    {
        using var client = factory.CreateClient();
        var workOrder = await ApiTestData.CreateWorkOrderAsync(client);

        using var planned = await client.PostAsJsonAsync($"/api/work-orders/{workOrder.Id}/status", new ChangeWorkOrderStatusRequest("Planned"), Cancellation);
        using var backToOpen = await client.PostAsJsonAsync($"/api/work-orders/{workOrder.Id}/status", new ChangeWorkOrderStatusRequest("Open"), Cancellation);

        Assert.Equal(HttpStatusCode.OK, planned.StatusCode);
        Assert.Equal("Planned", (await planned.Content.ReadFromJsonAsync<WorkOrderResponse>(Cancellation))!.Status);
        Assert.Equal(HttpStatusCode.Conflict, backToOpen.StatusCode);
    }

    [Fact]
    public async Task Post_visit_returns_201_and_its_status_can_change()
    {
        using var client = factory.CreateClient();
        var workOrder = await ApiTestData.CreateWorkOrderAsync(client);

        using var response = await client.PostAsJsonAsync($"/api/work-orders/{workOrder.Id}/visits",
            new CreateVisitRequest(Morning, Morning.AddHours(2), "Bring ladder"), Cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var visit = await response.Content.ReadFromJsonAsync<VisitResponse>(Cancellation);
        Assert.Equal("Planned", visit!.Status);
        Assert.Equal(Morning, visit.Start);

        using var started = await client.PostAsJsonAsync($"/api/visits/{visit.Id}/status", new ChangeVisitStatusRequest("InProgress"), Cancellation);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal("InProgress", (await client.GetFromJsonAsync<VisitResponse>($"/api/visits/{visit.Id}", Cancellation))!.Status);
        Assert.Equal(visit.Id, Assert.Single(await client.GetFromJsonAsync<VisitResponse[]>($"/api/work-orders/{workOrder.Id}/visits", Cancellation) ?? []).Id);
    }

    [Fact]
    public async Task Post_visit_with_an_invalid_time_range_returns_400()
    {
        using var client = factory.CreateClient();
        var workOrder = await ApiTestData.CreateWorkOrderAsync(client);

        using var response = await client.PostAsJsonAsync($"/api/work-orders/{workOrder.Id}/visits",
            new CreateVisitRequest(Morning, Morning, null), Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_visit_for_a_missing_work_order_returns_404()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync($"/api/work-orders/{Guid.NewGuid()}/visits",
            new CreateVisitRequest(Morning, Morning.AddHours(1), null), Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Operational_events_endpoint_returns_the_history_in_order()
    {
        using var client = factory.CreateClient();
        var workOrder = await ApiTestData.CreateWorkOrderAsync(client);
        (await client.PostAsJsonAsync($"/api/work-orders/{workOrder.Id}/status", new ChangeWorkOrderStatusRequest("Planned"), Cancellation)).EnsureSuccessStatusCode();
        using var visitResponse = await client.PostAsJsonAsync($"/api/work-orders/{workOrder.Id}/visits",
            new CreateVisitRequest(Morning, Morning.AddHours(1), null), Cancellation);
        var visit = await visitResponse.Content.ReadFromJsonAsync<VisitResponse>(Cancellation);
        (await client.PostAsJsonAsync($"/api/visits/{visit!.Id}/status", new ChangeVisitStatusRequest("InProgress"), Cancellation)).EnsureSuccessStatusCode();

        var workOrderEvents = await client.GetFromJsonAsync<OperationalEventResponse[]>($"/api/operations/events/work-order/{workOrder.Id}", Cancellation);
        var visitEvents = await client.GetFromJsonAsync<OperationalEventResponse[]>($"/api/operations/events/visit/{visit.Id}", Cancellation);

        Assert.NotNull(workOrderEvents);
        Assert.NotNull(visitEvents);
        Assert.Equal(["WorkOrderCreated", "WorkOrderStatusChanged"], workOrderEvents.Select(e => e.EventType));
        Assert.Equal("Planned", workOrderEvents[1].Payload.GetProperty("newStatus").GetString());
        Assert.Equal(["VisitCreated", "VisitStatusChanged"], visitEvents.Select(e => e.EventType));
        Assert.Equal(workOrder.Id, visitEvents[0].Payload.GetProperty("workOrderId").GetGuid());
        Assert.True(workOrderEvents[0].OccurredAtUtc <= workOrderEvents[1].OccurredAtUtc);
    }
}
