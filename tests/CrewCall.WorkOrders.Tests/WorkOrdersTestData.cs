using CrewCall.WorkOrders.Customers;
using CrewCall.WorkOrders.Sites;
using CrewCall.WorkOrders.Visits;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.WorkOrders.Tests;

/// <summary>Creates prerequisite records through the module services, with unique values.</summary>
internal static class WorkOrdersTestData
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public static async Task<Guid> CreateCustomerAsync(AsyncServiceScope scope)
    {
        var outcome = await scope.ServiceProvider.GetRequiredService<CustomerService>()
            .CreateAsync(new CreateCustomer($"Customer {Guid.NewGuid():N}", null), Cancellation);
        return Assert.IsType<CreateCustomerOutcome.Created>(outcome).Customer.Id;
    }

    public static async Task<Guid> CreateSiteAsync(AsyncServiceScope scope, Guid customerId)
    {
        var outcome = await scope.ServiceProvider.GetRequiredService<SiteService>()
            .CreateAsync(new CreateSite(customerId, "Plant 1", null, "Poznań", null, "PL", null, null), Cancellation);
        return Assert.IsType<CreateSiteOutcome.Created>(outcome).Site.Id;
    }

    public static async Task<WorkOrder> CreateWorkOrderAsync(AsyncServiceScope scope, string? priority = null)
    {
        var customerId = await CreateCustomerAsync(scope);
        var siteId = await CreateSiteAsync(scope, customerId);
        var outcome = await scope.ServiceProvider.GetRequiredService<WorkOrderService>()
            .CreateAsync(new CreateWorkOrder(customerId, siteId, "Replace inverter", null, priority), Cancellation);
        return Assert.IsType<CreateWorkOrderOutcome.Created>(outcome).WorkOrder;
    }

    public static async Task<Visit> CreateVisitAsync(AsyncServiceScope scope, Guid workOrderId)
    {
        var start = new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.FromHours(2));
        var outcome = await scope.ServiceProvider.GetRequiredService<VisitService>()
            .CreateAsync(new CreateVisit(workOrderId, start, start.AddHours(2), null), Cancellation);
        return Assert.IsType<CreateVisitOutcome.Created>(outcome).Visit;
    }
}
