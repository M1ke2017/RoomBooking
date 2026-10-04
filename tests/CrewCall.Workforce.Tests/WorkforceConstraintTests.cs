using CrewCall.Persistence;
using CrewCall.Workforce.Absences;
using CrewCall.Workforce.Holidays;
using CrewCall.Workforce.WorkingHours;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Workforce.Tests;

/// <summary>
/// The no-overlap and uniqueness rules are enforced by PostgreSQL too (exclusion constraints, unique index), so they hold
/// even for writes that skip the services' checks, such as two concurrent requests. These tests write directly.
/// </summary>
public sealed class WorkforceConstraintTests(WorkforceDatabase database)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private async Task SaveAsync(params object[] entities)
    {
        await using var scope = database.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrewCallDbContext>();
        db.AddRange(entities);
        await db.SaveChangesAsync(Cancellation);
    }

    private static TechnicianWorkingHours Hours(Guid technicianId, string start, string end) =>
        new(Guid.NewGuid(), technicianId, DayOfWeek.Monday, TimeOnly.Parse(start), TimeOnly.Parse(end));

    private static TechnicianAbsence Absence(Guid technicianId, int startHour, int endHour) =>
        new(Guid.NewGuid(), technicianId,
            new DateTimeOffset(2026, 7, 6, startHour, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 6, endHour, 0, 0, TimeSpan.Zero),
            AbsenceType.Other, null);

    [Fact]
    public async Task The_database_rejects_overlapping_working_hours_and_accepts_touching_ones()
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);
        await SaveAsync(Hours(technicianId, "20:00", "00:00")); // 20:00–24:00

        await SaveAsync(Hours(technicianId, "16:00", "20:00"));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(Hours(technicianId, "23:00", "23:30")));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(Hours(technicianId, "10:00", "16:30")));
    }

    [Fact]
    public async Task The_database_rejects_an_empty_or_reversed_working_hours_range()
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(Hours(technicianId, "16:00", "08:00")));
        await SaveAsync(Hours(technicianId, "00:00", "00:00")); // the whole day
    }

    [Fact]
    public async Task The_database_rejects_overlapping_absences_and_accepts_touching_ones()
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);
        await SaveAsync(Absence(technicianId, 8, 12));

        await SaveAsync(Absence(technicianId, 12, 14));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(Absence(technicianId, 11, 13)));
    }

    [Fact]
    public async Task The_database_allows_one_national_holiday_per_country_and_date()
    {
        var date = WorkforceTestData.UniqueHolidayDate();
        await SaveAsync(new HolidayCalendarEntry(Guid.NewGuid(), date, "National", "FR", null));

        await SaveAsync(new HolidayCalendarEntry(Guid.NewGuid(), date, "Regional", "FR", "IDF"));
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            SaveAsync(new HolidayCalendarEntry(Guid.NewGuid(), date, "National again", "FR", null)));
    }
}
