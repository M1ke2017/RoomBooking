using CrewCall.WorkOrders.Customers;
using CrewCall.WorkOrders.Executions;
using CrewCall.WorkOrders.Incidents;
using CrewCall.WorkOrders.Sites;
using CrewCall.WorkOrders.Visits;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CrewCall.WorkOrders;

public static class WorkOrdersModule
{
    /// <summary>
    /// Registers the WorkOrders module services. Requires registrations of <see cref="IWorkOrdersDbContext"/> and of the
    /// port <see cref="IActiveAssignmentCheck"/> (for field work).
    /// </summary>
    public static IServiceCollection AddWorkOrdersModule(this IServiceCollection services)
    {
        services.AddScoped<CustomerService>();
        services.AddScoped<SiteService>();
        services.AddScoped<WorkOrderService>();
        services.AddScoped<VisitService>();
        services.AddScoped<IncidentService>();
        services.AddScoped<VisitExecutionService>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
