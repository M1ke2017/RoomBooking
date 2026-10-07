using CrewCall.WorkOrders.Executions;

namespace CrewCall.Api.WorkOrdersAdapters;

internal static class WorkOrdersAdapterRegistration
{
    /// <summary>Connects the WorkOrders ports to the Scheduling module.</summary>
    public static IServiceCollection AddWorkOrdersAdapters(this IServiceCollection services)
    {
        services.AddScoped<IActiveAssignmentCheck, SchedulingActiveAssignmentCheck>();
        return services;
    }
}
