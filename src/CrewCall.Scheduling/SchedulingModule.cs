using CrewCall.Scheduling.Assignments;
using CrewCall.Scheduling.Checks;
using CrewCall.Scheduling.Incidents;
using CrewCall.Scheduling.Matching;
using CrewCall.Scheduling.Reservations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CrewCall.Scheduling;

public static class SchedulingModule
{
    /// <summary>
    /// Registers the Scheduling module services. Requires registrations of <see cref="ISchedulingDbContext"/> and of the
    /// ports <see cref="Ports.ITechnicianSchedulingSource"/>, <see cref="Ports.IResourceCatalog"/> and
    /// <see cref="Ports.IVisitSchedulingSource"/> (and <see cref="Ports.IIncidentWorkOrders"/> for the incident workflow).
    /// </summary>
    public static IServiceCollection AddSchedulingModule(this IServiceCollection services)
    {
        services.AddScoped<ResourceReservationService>();
        services.AddScoped<SchedulingCheckService>();
        services.AddScoped<AssignmentService>();
        services.AddScoped<ResourceMatchingService>();
        services.AddScoped<UrgentIncidentService>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
