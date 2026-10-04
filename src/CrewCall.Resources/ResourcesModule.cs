using CrewCall.Resources.Equipment;
using CrewCall.Resources.Vehicles;
using Microsoft.Extensions.DependencyInjection;

namespace CrewCall.Resources;

public static class ResourcesModule
{
    /// <summary>Registers the Resources module services. Requires an <see cref="IResourcesDbContext"/> registration.</summary>
    public static IServiceCollection AddResourcesModule(this IServiceCollection services)
    {
        services.AddScoped<VehicleService>();
        services.AddScoped<EquipmentService>();
        return services;
    }
}
