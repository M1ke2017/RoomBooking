using CrewCall.Workforce.TimeZones;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.Workforce.Holidays;

/// <summary>A holiday calendar maintained through the API. No external holiday source is used.</summary>
public sealed class HolidayService(IWorkforceDbContext db)
{
    public async Task<CreateHolidayOutcome> CreateAsync(CreateHoliday command, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();

        if (command.Date is null)
        {
            errors.Add("date", "Required.");
        }

        var name = errors.Required("name", command.Name, HolidayCalendarEntry.NameMaxLength);
        var countryCode = errors.CountryCode("countryCode", command.CountryCode, required: true);
        var regionCode = errors.Optional("regionCode", command.RegionCode, HolidayCalendarEntry.RegionCodeMaxLength)?.ToUpperInvariant();

        if (regionCode is not null && !regionCode.All(char.IsAsciiLetterOrDigit))
        {
            errors.Add("regionCode", "Must be the ISO 3166-2 subdivision part: 1–3 letters or digits, e.g. BY.");
        }

        if (errors.Any)
        {
            return new CreateHolidayOutcome.Invalid(errors.ToDictionary());
        }

        var date = command.Date!.Value;
        if (await FindAsync(countryCode!, regionCode, date, cancellationToken) is { } existingId)
        {
            return new CreateHolidayOutcome.AlreadyExists(existingId);
        }

        var holiday = new HolidayCalendarEntry(Guid.CreateVersion7(), date, name!, countryCode!, regionCode);
        db.HolidayCalendar.Add(holiday);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent request may have added the same holiday; the unique index rejected this one.
            db.HolidayCalendar.Entry(holiday).State = EntityState.Detached;
            if (await FindAsync(countryCode!, regionCode, date, cancellationToken) is { } concurrentId)
            {
                return new CreateHolidayOutcome.AlreadyExists(concurrentId);
            }

            throw;
        }

        return new CreateHolidayOutcome.Created(holiday);
    }

    /// <summary>All holidays ordered by date, or only one country's when <paramref name="countryCode"/> is given.</summary>
    public async Task<ListHolidaysOutcome> ListAsync(string? countryCode, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var code = errors.CountryCode("countryCode", countryCode, required: false);
        if (errors.Any)
        {
            return new ListHolidaysOutcome.Invalid(errors.ToDictionary());
        }

        var query = db.HolidayCalendar.AsNoTracking();
        if (code is not null)
        {
            query = query.Where(holiday => holiday.CountryCode == code);
        }

        var holidays = await query
            .OrderBy(holiday => holiday.Date)
            .ThenBy(holiday => holiday.CountryCode)
            .ThenBy(holiday => holiday.RegionCode)
            .ToListAsync(cancellationToken);

        return new ListHolidaysOutcome.Listed(holidays);
    }

    private Task<Guid?> FindAsync(string countryCode, string? regionCode, DateOnly date, CancellationToken cancellationToken) =>
        db.HolidayCalendar
            .Where(holiday => holiday.CountryCode == countryCode && holiday.RegionCode == regionCode && holiday.Date == date)
            .Select(holiday => (Guid?)holiday.Id)
            .FirstOrDefaultAsync(cancellationToken);
}
