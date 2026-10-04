using System.Text.RegularExpressions;
using CrewCall.Workforce.TimeZones;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.Workforce.Technicians;

public sealed partial class TechnicianService(IWorkforceDbContext db)
{
    public async Task<CreateTechnicianOutcome> CreateAsync(CreateTechnician command, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var displayName = errors.Required("displayName", command.DisplayName, Technician.DisplayNameMaxLength);
        var email = errors.Required("email", command.Email, Technician.EmailMaxLength)?.ToLowerInvariant();

        if (email is not null && !BasicEmailFormat().IsMatch(email))
        {
            errors.Add("email", "Must be a valid email address.");
        }

        var timeZoneId = errors.TimeZoneId("timeZoneId", command.TimeZoneId);
        var countryCode = errors.CountryCode("countryCode", command.CountryCode, required: true);

        if (errors.Any)
        {
            return new CreateTechnicianOutcome.Invalid(errors.ToDictionary());
        }

        if (await EmailExistsAsync(email!, cancellationToken))
        {
            return new CreateTechnicianOutcome.EmailAlreadyExists(email!);
        }

        var technician = new Technician(
            Guid.CreateVersion7(), displayName!, email!, command.IsActive ?? true, timeZoneId!, countryCode!);
        db.Technicians.Add(technician);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent request may have registered the same email between the check and the insert,
            // in which case the unique index rejected this one. Anything else is a genuine failure.
            if (await EmailExistsAsync(email!, cancellationToken))
            {
                return new CreateTechnicianOutcome.EmailAlreadyExists(email!);
            }

            throw;
        }

        return new CreateTechnicianOutcome.Created(technician);
    }

    public async Task<IReadOnlyList<Technician>> ListAsync(CancellationToken cancellationToken) =>
        await db.Technicians
            .AsNoTracking()
            .OrderBy(technician => technician.DisplayName)
            .ThenBy(technician => technician.Id)
            .ToListAsync(cancellationToken);

    private Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken) =>
        db.Technicians.AnyAsync(technician => technician.Email == email, cancellationToken);

    // Deliberately basic: one "@", no whitespace, a dot in the domain. Real verification is out of scope.
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex BasicEmailFormat();
}
