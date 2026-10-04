using Microsoft.EntityFrameworkCore;

namespace CrewCall.Workforce.Skills;

public sealed class SkillService(IWorkforceDbContext db)
{
    public async Task<CreateSkillOutcome> CreateAsync(CreateSkill command, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var name = errors.Required("name", command.Name, Skill.NameMaxLength);
        var code = errors.Optional("code", command.Code, Skill.CodeMaxLength)?.ToUpperInvariant();

        if (errors.Any)
        {
            return new CreateSkillOutcome.Invalid(errors.ToDictionary());
        }

        if (code is not null && await CodeExistsAsync(code, cancellationToken))
        {
            return new CreateSkillOutcome.CodeAlreadyExists(code);
        }

        var skill = new Skill(Guid.CreateVersion7(), name!, code, command.IsActive ?? true);
        db.Skills.Add(skill);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent request may have taken the same code; the unique index rejected this one.
            if (code is not null && await CodeExistsAsync(code, cancellationToken))
            {
                return new CreateSkillOutcome.CodeAlreadyExists(code);
            }

            throw;
        }

        return new CreateSkillOutcome.Created(skill);
    }

    public async Task<IReadOnlyList<Skill>> ListAsync(CancellationToken cancellationToken) =>
        await db.Skills
            .AsNoTracking()
            .OrderBy(skill => skill.Name)
            .ThenBy(skill => skill.Id)
            .ToListAsync(cancellationToken);

    public async Task<AssignSkillOutcome> AssignToTechnicianAsync(Guid technicianId, Guid skillId, CancellationToken cancellationToken)
    {
        if (!await db.Technicians.AnyAsync(technician => technician.Id == technicianId, cancellationToken))
        {
            return new AssignSkillOutcome.TechnicianNotFound(technicianId);
        }

        var skill = await db.Skills.AsNoTracking().SingleOrDefaultAsync(s => s.Id == skillId, cancellationToken);
        if (skill is null)
        {
            return new AssignSkillOutcome.SkillNotFound(skillId);
        }

        if (await IsAssignedAsync(technicianId, skillId, cancellationToken))
        {
            return new AssignSkillOutcome.AlreadyAssigned(skill);
        }

        db.TechnicianSkills.Add(new TechnicianSkill(technicianId, skillId));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent request assigned the same skill; the composite primary key rejected the duplicate.
            if (await IsAssignedAsync(technicianId, skillId, cancellationToken))
            {
                return new AssignSkillOutcome.AlreadyAssigned(skill);
            }

            throw;
        }

        return new AssignSkillOutcome.Assigned(skill);
    }

    public async Task<RemoveSkillOutcome> RemoveFromTechnicianAsync(Guid technicianId, Guid skillId, CancellationToken cancellationToken)
    {
        if (!await db.Technicians.AnyAsync(technician => technician.Id == technicianId, cancellationToken))
        {
            return new RemoveSkillOutcome.TechnicianNotFound(technicianId);
        }

        if (!await db.Skills.AnyAsync(skill => skill.Id == skillId, cancellationToken))
        {
            return new RemoveSkillOutcome.SkillNotFound(skillId);
        }

        await db.TechnicianSkills
            .Where(link => link.TechnicianId == technicianId && link.SkillId == skillId)
            .ExecuteDeleteAsync(cancellationToken);

        return new RemoveSkillOutcome.Removed();
    }

    /// <summary>Returns the technician's skills, or null when the technician does not exist.</summary>
    public async Task<IReadOnlyList<Skill>?> ListForTechnicianAsync(Guid technicianId, CancellationToken cancellationToken)
    {
        if (!await db.Technicians.AnyAsync(technician => technician.Id == technicianId, cancellationToken))
        {
            return null;
        }

        return await db.Skills
            .AsNoTracking()
            .Where(skill => db.TechnicianSkills.Any(link => link.TechnicianId == technicianId && link.SkillId == skill.Id))
            .OrderBy(skill => skill.Name)
            .ThenBy(skill => skill.Id)
            .ToListAsync(cancellationToken);
    }

    private Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken) =>
        db.Skills.AnyAsync(skill => skill.Code == code, cancellationToken);

    private Task<bool> IsAssignedAsync(Guid technicianId, Guid skillId, CancellationToken cancellationToken) =>
        db.TechnicianSkills.AnyAsync(link => link.TechnicianId == technicianId && link.SkillId == skillId, cancellationToken);
}
