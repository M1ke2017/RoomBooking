using CrewCall.Workforce.Technicians;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.Workforce.Teams;

public sealed class TeamService(IWorkforceDbContext db)
{
    public async Task<CreateTeamOutcome> CreateAsync(CreateTeam command, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var name = errors.Required("name", command.Name, Team.NameMaxLength);

        if (errors.Any)
        {
            return new CreateTeamOutcome.Invalid(errors.ToDictionary());
        }

        var team = new Team(Guid.CreateVersion7(), name!, command.IsActive ?? true);
        db.Teams.Add(team);
        await db.SaveChangesAsync(cancellationToken);

        return new CreateTeamOutcome.Created(team);
    }

    public async Task<IReadOnlyList<Team>> ListAsync(CancellationToken cancellationToken) =>
        await db.Teams
            .AsNoTracking()
            .OrderBy(team => team.Name)
            .ThenBy(team => team.Id)
            .ToListAsync(cancellationToken);

    public async Task<AddTeamMemberOutcome> AddMemberAsync(Guid teamId, Guid technicianId, CancellationToken cancellationToken)
    {
        if (!await db.Teams.AnyAsync(team => team.Id == teamId, cancellationToken))
        {
            return new AddTeamMemberOutcome.TeamNotFound(teamId);
        }

        var technician = await db.Technicians.SingleOrDefaultAsync(t => t.Id == technicianId, cancellationToken);
        if (technician is null)
        {
            return new AddTeamMemberOutcome.TechnicianNotFound(technicianId);
        }

        if (technician.TeamId == teamId)
        {
            return new AddTeamMemberOutcome.AlreadyMember(technician);
        }

        if (technician.TeamId is { } currentTeamId)
        {
            // Never move a technician between teams silently; reassignment is a separate decision.
            return new AddTeamMemberOutcome.MemberOfAnotherTeam(technicianId, currentTeamId);
        }

        technician.JoinTeam(teamId);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Technicians carry a row version: a concurrent request changed this technician's team first.
            var savedTeamId = await db.Technicians
                .Where(t => t.Id == technicianId)
                .Select(t => t.TeamId)
                .SingleAsync(cancellationToken);

            if (savedTeamId == teamId)
            {
                return new AddTeamMemberOutcome.AlreadyMember(technician);
            }

            if (savedTeamId is { } otherTeamId)
            {
                return new AddTeamMemberOutcome.MemberOfAnotherTeam(technicianId, otherTeamId);
            }

            throw;
        }

        return new AddTeamMemberOutcome.Added(technician);
    }

    public async Task<RemoveTeamMemberOutcome> RemoveMemberAsync(Guid teamId, Guid technicianId, CancellationToken cancellationToken)
    {
        if (!await db.Teams.AnyAsync(team => team.Id == teamId, cancellationToken))
        {
            return new RemoveTeamMemberOutcome.TeamNotFound(teamId);
        }

        var technician = await db.Technicians.SingleOrDefaultAsync(t => t.Id == technicianId, cancellationToken);
        if (technician is null)
        {
            return new RemoveTeamMemberOutcome.TechnicianNotFound(technicianId);
        }

        if (technician.TeamId == teamId)
        {
            technician.LeaveTeam();
            await db.SaveChangesAsync(cancellationToken);
        }

        return new RemoveTeamMemberOutcome.Removed();
    }

    /// <summary>Returns the team's technicians, or null when the team does not exist.</summary>
    public async Task<IReadOnlyList<Technician>?> ListMembersAsync(Guid teamId, CancellationToken cancellationToken)
    {
        if (!await db.Teams.AnyAsync(team => team.Id == teamId, cancellationToken))
        {
            return null;
        }

        return await db.Technicians
            .AsNoTracking()
            .Where(technician => technician.TeamId == teamId)
            .OrderBy(technician => technician.DisplayName)
            .ThenBy(technician => technician.Id)
            .ToListAsync(cancellationToken);
    }
}
