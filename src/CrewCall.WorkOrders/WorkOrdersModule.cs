using CrewCall.WorkOrders.Customers;
using CrewCall.WorkOrders.Sites;
using Microsoft.Extensions.DependencyInjection;

namespace CrewCall.WorkOrders;

public static class WorkOrdersModule
{
    /// <summary>Registers the WorkOrders module services. Requires an <see cref="IWorkOrdersDbContext"/> registration.</summary>
    public static IServiceCollection AddWorkOrdersModule(this IServiceCollection services)
    {
        services.AddScoped<CustomerService>();
        services.AddScoped<SiteService>();
        return services;
    }
}
