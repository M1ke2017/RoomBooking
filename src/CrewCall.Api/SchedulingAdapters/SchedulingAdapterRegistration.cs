using CrewCall.Scheduling.Ports;

namespace CrewCall.Api.SchedulingAdapters;

internal static class SchedulingAdapterRegistration
{
    /// <summary>Connects the Scheduling ports to the Workforce and Resources modules.</summary>
    public static IServiceCollection AddSchedulingAdapters(this IServiceCollection services)
    {
        services.AddScoped<ITechnicianSchedulingSource, WorkforceTechnicianSchedulingSource>();
        services.AddScoped<IResourceCatalog, ResourcesCatalog>();
        return services;
    }
}
