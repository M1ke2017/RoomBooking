using CrewCall.Workforce.Technicians;
using Microsoft.Extensions.DependencyInjection;

namespace CrewCall.Workforce;

public static class WorkforceModule
{
    /// <summary>Registers the Workforce module services. Requires an <see cref="IWorkforceDbContext"/> registration.</summary>
    public static IServiceCollection AddWorkforceModule(this IServiceCollection services)
    {
        services.AddScoped<TechnicianService>();
        return services;
    }
}
