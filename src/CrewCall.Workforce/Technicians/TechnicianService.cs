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
        var phoneNumber = PhoneNumberOf(errors, command.PhoneNumber);

        if (errors.Any)
        {
            return new CreateTechnicianOutcome.Invalid(errors.ToDictionary());
        }

        if (await EmailExistsAsync(email!, cancellationToken))
        {
            return new CreateTechnicianOutcome.EmailAlreadyExists(email!);
        }

        var technician = new Technician(
            Guid.CreateVersion7(), displayName!, email!, command.IsActive ?? true, timeZoneId!, countryCode!, phoneNumber);
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

    /// <summary>
    /// Normalizes an optional phone number to E.164: "+" then 7–15 digits, no leading zero. Spaces, dashes, dots and
    /// brackets are removed. False when a number is given but is not E.164.
    /// </summary>
    internal static bool TryNormalizePhoneNumber(string? value, out string? phoneNumber)
    {
        phoneNumber = string.IsNullOrWhiteSpace(value) ? null : PhoneSeparators().Replace(value.Trim(), string.Empty);
        return phoneNumber is null || E164().IsMatch(phoneNumber);
    }

    private static string? PhoneNumberOf(ValidationErrors errors, string? value)
    {
        if (TryNormalizePhoneNumber(value, out var phoneNumber))
        {
            return phoneNumber;
        }

        errors.Add("phoneNumber", "Must be an E.164 phone number, e.g. +48601234567.");
        return null;
    }

    [GeneratedRegex(@"[\s\-.()]")]
    private static partial Regex PhoneSeparators();

    [GeneratedRegex(@"^\+[1-9][0-9]{6,14}$")]
    private static partial Regex E164();

    // Deliberately basic: one "@", no whitespace, a dot in the domain. Real verification is out of scope.
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex BasicEmailFormat();
}
