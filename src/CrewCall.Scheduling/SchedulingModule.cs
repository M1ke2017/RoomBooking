using CrewCall.Scheduling.Checks;
using CrewCall.Scheduling.Reservations;
using Microsoft.Extensions.DependencyInjection;

namespace CrewCall.Scheduling;

public static class SchedulingModule
{
    /// <summary>
    /// Registers the Scheduling module services. Requires registrations of <see cref="ISchedulingDbContext"/> and of the
    /// ports <see cref="Ports.ITechnicianSchedulingSource"/> and <see cref="Ports.IResourceCatalog"/>.
    /// </summary>
    public static IServiceCollection AddSchedulingModule(this IServiceCollection services)
    {
        services.AddScoped<ResourceReservationService>();
        services.AddScoped<SchedulingCheckService>();
        return services;
    }
}
