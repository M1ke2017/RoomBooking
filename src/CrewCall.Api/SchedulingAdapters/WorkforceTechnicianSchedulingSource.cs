using CrewCall.Scheduling.Ports;
using CrewCall.Workforce;
using CrewCall.Workforce.Availability;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.Api.SchedulingAdapters;

/// <summary>
/// Provides Scheduling with Workforce data. Lives in the composition root because modules do not reference each other
/// (ADR-0001): Scheduling owns the port, Workforce owns the data and the availability rules, and this adapter only
/// translates between them. Availability is Workforce's own decision (WorkforceAvailabilityService); nothing is
/// re-implemented here.
/// </summary>
internal sealed class WorkforceTechnicianSchedulingSource(IWorkforceDbContext workforce, WorkforceAvailabilityService availability)
    : ITechnicianSchedulingSource
{
    public async Task<TechnicianSchedulingProfile?> GetProfileAsync(
        Guid technicianId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var outcome = await availability.CheckAsync(new CheckAvailability(technicianId, start, end), cancellationToken);
        if (outcome is CheckAvailabilityOutcome.TechnicianNotFound)
        {
            return null;
        }

        if (outcome is not CheckAvailabilityOutcome.Evaluated evaluated)
        {
            // Scheduling validates the interval with the same limits before calling; reaching this is a bug.
            throw new InvalidOperationException($"Workforce rejected the availability query: {outcome}.");
        }

        var isActive = await workforce.Technicians
            .Where(technician => technician.Id == technicianId)
            .Select(technician => technician.IsActive)
            .SingleAsync(cancellationToken);

        // Only active skills with a code can satisfy a required skill code.
        var skillCodes = await workforce.Skills
            .Where(skill => skill.IsActive && skill.Code != null
                && workforce.TechnicianSkills.Any(link => link.TechnicianId == technicianId && link.SkillId == skill.Id))
            .Select(skill => skill.Code!)
            .ToListAsync(cancellationToken);

        var decision = evaluated.Availability.Decision;
        return new TechnicianSchedulingProfile(
            technicianId,
            isActive,
            skillCodes,
            decision.Status switch
            {
                AvailabilityStatus.Available => TechnicianAvailabilityState.Available,
                AvailabilityStatus.UnavailableInactiveTechnician => TechnicianAvailabilityState.Inactive,
                AvailabilityStatus.UnavailableHoliday => TechnicianAvailabilityState.Holiday,
                AvailabilityStatus.UnavailableAbsence => TechnicianAvailabilityState.Absence,
                AvailabilityStatus.UnavailableOutsideWorkingHours => TechnicianAvailabilityState.OutsideWorkingHours,
                _ => throw new InvalidOperationException($"Unhandled availability status {decision.Status}.")
            },
            decision.Detail);
    }

    public Task<bool> ExistsAsync(Guid technicianId, CancellationToken cancellationToken) =>
        workforce.Technicians.AnyAsync(technician => technician.Id == technicianId, cancellationToken);

    public async Task<IReadOnlyList<TechnicianSummary>> ListTechniciansAsync(
        IReadOnlyCollection<Guid>? technicianIds, int limit, CancellationToken cancellationToken)
    {
        var query = workforce.Technicians.AsNoTracking();
        if (technicianIds is not null)
        {
            var ids = technicianIds.ToList();
            query = query.Where(technician => ids.Contains(technician.Id));
        }
        else
        {
            query = query.Where(technician => technician.IsActive);
        }

        return await query
            .OrderBy(technician => technician.Id)
            .Take(limit)
            .Select(technician => new TechnicianSummary(technician.Id, technician.DisplayName, technician.IsActive, technician.TeamId))
            .ToListAsync(cancellationToken);
    }

    public Task<bool> TeamExistsAsync(Guid teamId, CancellationToken cancellationToken) =>
        workforce.Teams.AnyAsync(team => team.Id == teamId, cancellationToken);
}
