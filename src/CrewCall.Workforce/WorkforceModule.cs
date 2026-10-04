using CrewCall.Workforce.Absences;
using CrewCall.Workforce.Availability;
using CrewCall.Workforce.Holidays;
using CrewCall.Workforce.Skills;
using CrewCall.Workforce.Teams;
using CrewCall.Workforce.Technicians;
using CrewCall.Workforce.WorkingHours;
using Microsoft.Extensions.DependencyInjection;

namespace CrewCall.Workforce;

public static class WorkforceModule
{
    /// <summary>Registers the Workforce module services. Requires an <see cref="IWorkforceDbContext"/> registration.</summary>
    public static IServiceCollection AddWorkforceModule(this IServiceCollection services)
    {
        services.AddScoped<TechnicianService>();
        services.AddScoped<SkillService>();
        services.AddScoped<TeamService>();
        services.AddScoped<WorkingHoursService>();
        services.AddScoped<AbsenceService>();
        services.AddScoped<HolidayService>();
        services.AddScoped<WorkforceAvailabilityService>();
        return services;
    }
}
