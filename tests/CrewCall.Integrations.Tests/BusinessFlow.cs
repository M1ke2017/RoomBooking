using CrewCall.Persistence;
using CrewCall.WorkOrders;
using CrewCall.WorkOrders.Customers;
using CrewCall.WorkOrders.Executions;
using CrewCall.WorkOrders.Sites;
using CrewCall.WorkOrders.Visits;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Integrations.Tests;

/// <summary>Business operations through the WorkOrders module, as the API performs them: the write side under test.</summary>
internal static class BusinessFlow
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A customer, site, work order and visit. Returns the visit and its site.</summary>
    public static async Task<(Guid VisitId, Guid SiteId)> CreateVisitAsync(ServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var customer = Assert.IsType<CreateCustomerOutcome.Created>(
            await provider.GetRequiredService<CustomerService>().CreateAsync(new CreateCustomer($"Customer {Guid.NewGuid():N}", null), Cancellation)).Customer;
        var site = Assert.IsType<CreateSiteOutcome.Created>(await provider.GetRequiredService<SiteService>()
            .CreateAsync(new CreateSite(customer.Id, "Plant 1", null, "Poznań", null, "PL", null, null), Cancellation)).Site;
        var workOrder = Assert.IsType<CreateWorkOrderOutcome.Created>(await provider.GetRequiredService<WorkOrderService>()
            .CreateAsync(new CreateWorkOrder(customer.Id, site.Id, "Replace inverter", null, null), Cancellation)).WorkOrder;
        var start = new DateTimeOffset(2038, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var visit = Assert.IsType<CreateVisitOutcome.Created>(await provider.GetRequiredService<VisitService>()
            .CreateAsync(new CreateVisit(workOrder.Id, start, start.AddHours(1), null), Cancellation)).Visit;
        return (visit.Id, site.Id);
    }

    /// <summary>Starts and completes field work on the visit: commits visit.work-completed to the outbox.</summary>
    public static async Task CompleteWorkAsync(ServiceProvider services, Guid visitId)
    {
        await using var scope = services.CreateAsyncScope();
        var execution = scope.ServiceProvider.GetRequiredService<VisitExecutionService>();
        Assert.IsType<VisitExecutionOutcome.Changed>(await execution.StartWorkAsync(visitId, Cancellation));
        Assert.IsType<VisitExecutionOutcome.Changed>(await execution.CompleteAsync(visitId, Cancellation));
    }

    public static async Task<(Guid VisitId, Guid SiteId)> CompleteVisitWorkAsync(ServiceProvider services)
    {
        var visit = await CreateVisitAsync(services);
        await CompleteWorkAsync(services, visit.VisitId);
        return visit;
    }

    /// <summary>
    /// An assignment row for the visit, written directly: the routing lookup only reads assignments, and claiming one
    /// through the Scheduling module needs the API's adapters.
    /// </summary>
    public static async Task<Guid> InsertAssignmentAsync(ServiceProvider services, Guid visitId, Guid technicianId, string status)
    {
        var id = Guid.CreateVersion7();
        var start = new DateTimeOffset(2038, 6, 1, 9, 0, 0, TimeSpan.Zero);
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().Database.ExecuteSqlAsync(
            $"""
            INSERT INTO scheduling.assignments (id, visit_id, technician_id, travel_buffer_before_minutes, travel_buffer_after_minutes,
                claimed_start, claimed_end, status, created_at_utc, updated_at_utc)
            VALUES ({id}, {visitId}, {technicianId}, 0, 0, {start}, {start.AddHours(1)}, {status}, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow})
            """,
            Cancellation);
        return id;
    }
}
