using CrewCall.WorkOrders.Customers;
using CrewCall.WorkOrders.Incidents;
using CrewCall.WorkOrders.Sites;
using CrewCall.WorkOrders.Visits;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CrewCall.WorkOrders;

public static class WorkOrdersModule
{
    /// <summary>Registers the WorkOrders module services. Requires an <see cref="IWorkOrdersDbContext"/> registration.</summary>
    public static IServiceCollection AddWorkOrdersModule(this IServiceCollection services)
    {
        services.AddScoped<CustomerService>();
        services.AddScoped<SiteService>();
        services.AddScoped<WorkOrderService>();
        services.AddScoped<VisitService>();
        services.AddScoped<IncidentService>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
