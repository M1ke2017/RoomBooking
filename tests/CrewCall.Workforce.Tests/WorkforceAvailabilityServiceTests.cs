using CrewCall.Workforce.Absences;
using CrewCall.Workforce.Availability;
using CrewCall.Workforce.Holidays;
using CrewCall.Workforce.WorkingHours;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Workforce.Tests;

/// <summary>The availability service end to end: data written through the module services, read from PostgreSQL.</summary>
public sealed class WorkforceAvailabilityServiceTests(WorkforceDatabase database)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private async Task AddWorkingHoursAsync(Guid technicianId, string day, string start, string end)
    {
        await using var scope = database.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<WorkingHoursService>()
            .CreateAsync(new CreateWorkingHours(technicianId, day, start, end), Cancellation);
        Assert.IsType<CreateWorkingHoursOutcome.Created>(outcome);
    }

    private async Task AddAbsenceAsync(Guid technicianId, DateTimeOffset start, DateTimeOffset end)
    {
        await using var scope = database.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<AbsenceService>()
            .CreateAsync(new CreateAbsence(technicianId, start, end, "Training", null), Cancellation);
        Assert.IsType<CreateAbsenceOutcome.Created>(outcome);
    }

    private async Task<CheckAvailabilityOutcome> CheckAsync(Guid technicianId, DateTimeOffset? start, DateTimeOffset? end)
    {
        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkforceAvailabilityService>()
            .CheckAsync(new CheckAvailability(technicianId, start, end), Cancellation);
    }

    private async Task<TechnicianAvailability> EvaluateAsync(Guid technicianId, DateTimeOffset start, DateTimeOffset end) =>
        Assert.IsType<CheckAvailabilityOutcome.Evaluated>(await CheckAsync(technicianId, start, end)).Availability;

    [Fact]
    public async Task An_unknown_technician_is_reported_as_not_found()
    {
        var outcome = await CheckAsync(Guid.NewGuid(), Utc(2026, 7, 6, 8), Utc(2026, 7, 6, 9));

        Assert.IsType<CheckAvailabilityOutcome.TechnicianNotFound>(outcome);
    }

    [Fact]
    public async Task Missing_reversed_or_too_long_intervals_are_invalid()
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);

        var missing = Assert.IsType<CheckAvailabilityOutcome.Invalid>(await CheckAsync(technicianId, null, null));
        Assert.Equal(["end", "start"], missing.Errors.Keys.Order());

        var reversed = Assert.IsType<CheckAvailabilityOutcome.Invalid>(await CheckAsync(technicianId, Utc(2026, 7, 6, 9), Utc(2026, 7, 6, 8)));
        Assert.Equal(["end"], reversed.Errors.Keys);

        var tooLong = Assert.IsType<CheckAvailabilityOutcome.Invalid>(await CheckAsync(technicianId, Utc(2026, 7, 1, 0), Utc(2026, 8, 2, 0)));
        Assert.Equal(["end"], tooLong.Errors.Keys);
    }

    [Fact]
    public async Task An_inactive_technician_is_unavailable_even_inside_working_hours()
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database, isActive: false);
        await AddWorkingHoursAsync(technicianId, "Monday", "08:00", "16:00");

        var availability = await EvaluateAsync(technicianId, Utc(2026, 7, 6, 7), Utc(2026, 7, 6, 8));

        Assert.False(availability.Decision.IsAvailable);
        Assert.Equal(AvailabilityStatus.UnavailableInactiveTechnician, availability.Decision.Status);
    }

    [Fact]
    public async Task Inside_outside_and_partial_intervals_are_evaluated_against_local_working_hours()
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);
        await AddWorkingHoursAsync(technicianId, "Monday", "08:00", "16:00"); // 06:00Z–14:00Z in July

        var inside = await EvaluateAsync(technicianId, Utc(2026, 7, 6, 6), Utc(2026, 7, 6, 14));
        Assert.True(inside.Decision.IsAvailable);
        Assert.Equal(AvailabilityStatus.Available, inside.Decision.Status);
        Assert.Equal("Europe/Warsaw", inside.TimeZoneId);
        Assert.Equal(new DateTimeOffset(2026, 7, 6, 8, 0, 0, TimeSpan.FromHours(2)), inside.LocalStart);
        Assert.Equal(TimeSpan.FromHours(2), inside.LocalStart.Offset);

        var outside = await EvaluateAsync(technicianId, Utc(2026, 7, 6, 15), Utc(2026, 7, 6, 16));
        Assert.Equal(AvailabilityStatus.UnavailableOutsideWorkingHours, outside.Decision.Status);

        var partial = await EvaluateAsync(technicianId, Utc(2026, 7, 6, 13), Utc(2026, 7, 6, 15));
        Assert.Equal(AvailabilityStatus.UnavailableOutsideWorkingHours, partial.Decision.Status);
    }

    [Fact]
    public async Task An_overlapping_absence_blocks_but_a_touching_one_does_not()
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);
        await AddWorkingHoursAsync(technicianId, "Monday", "08:00", "16:00");
        await AddAbsenceAsync(technicianId, Utc(2026, 7, 6, 6), Utc(2026, 7, 6, 9));

        var overlapping = await EvaluateAsync(technicianId, Utc(2026, 7, 6, 8), Utc(2026, 7, 6, 10));
        Assert.Equal(AvailabilityStatus.UnavailableAbsence, overlapping.Decision.Status);
        Assert.Equal("Training", overlapping.Decision.Detail);

        var touching = await EvaluateAsync(technicianId, Utc(2026, 7, 6, 9), Utc(2026, 7, 6, 10));
        Assert.Equal(AvailabilityStatus.Available, touching.Decision.Status);
    }

    [Fact]
    public async Task A_national_holiday_of_the_technicians_country_blocks_but_another_countrys_does_not()
    {
        var date = WorkforceTestData.UniqueHolidayDate();
        var polishId = await WorkforceTestData.CreateTechnicianAsync(database);
        var germanId = await WorkforceTestData.CreateTechnicianAsync(database, "Europe/Berlin", "DE");
        foreach (var technicianId in new[] { polishId, germanId })
        {
            foreach (var day in Enum.GetNames<DayOfWeek>())
            {
                await AddWorkingHoursAsync(technicianId, day, "00:00", "24:00");
            }
        }

        await using (var scope = database.CreateScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<HolidayService>()
                .CreateAsync(new CreateHoliday(date, "Święto testowe", "PL", null), Cancellation);
            Assert.IsType<CreateHolidayOutcome.Created>(outcome);
        }

        // 10:00Z–11:00Z on the holiday is midday locally in both Warsaw and Berlin.
        var start = new DateTimeOffset(date.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
        var polish = await EvaluateAsync(polishId, start, start.AddHours(1));
        var german = await EvaluateAsync(germanId, start, start.AddHours(1));

        Assert.Equal(AvailabilityStatus.UnavailableHoliday, polish.Decision.Status);
        Assert.Contains("Święto testowe", polish.Decision.Detail);
        Assert.Equal(AvailabilityStatus.Available, german.Decision.Status);
    }

    [Fact]
    public async Task Spring_forward_is_applied_on_the_day_it_happens()
    {
        // Europe/Warsaw, Sunday 2026-03-29: 08:00–16:00 local is 06:00Z–14:00Z (CEST); on Saturday it was 07:00Z–15:00Z.
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);
        await AddWorkingHoursAsync(technicianId, "Saturday", "08:00", "16:00");
        await AddWorkingHoursAsync(technicianId, "Sunday", "08:00", "16:00");

        var saturday = await EvaluateAsync(technicianId, Utc(2026, 3, 28, 7), Utc(2026, 3, 28, 15));
        var sunday = await EvaluateAsync(technicianId, Utc(2026, 3, 29, 6), Utc(2026, 3, 29, 14));
        var sundayWithSaturdayHours = await EvaluateAsync(technicianId, Utc(2026, 3, 29, 7), Utc(2026, 3, 29, 15));

        Assert.Equal(AvailabilityStatus.Available, saturday.Decision.Status);
        Assert.Equal(TimeSpan.FromHours(1), saturday.LocalStart.Offset);
        Assert.Equal(AvailabilityStatus.Available, sunday.Decision.Status);
        Assert.Equal(TimeSpan.FromHours(2), sunday.LocalStart.Offset);
        Assert.Equal(AvailabilityStatus.UnavailableOutsideWorkingHours, sundayWithSaturdayHours.Decision.Status);
    }

    [Fact]
    public async Task Fall_back_night_shift_covers_seven_real_hours()
    {
        // Europe/Warsaw, Sunday 2026-10-25 00:00–06:00 local: 2026-10-24T22:00Z (CEST) to 05:00Z (CET).
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);
        await AddWorkingHoursAsync(technicianId, "Sunday", "00:00", "06:00");

        var whole = await EvaluateAsync(technicianId, Utc(2026, 10, 24, 22), Utc(2026, 10, 25, 5));
        var beyond = await EvaluateAsync(technicianId, Utc(2026, 10, 24, 22), Utc(2026, 10, 25, 5, 30));

        Assert.Equal(AvailabilityStatus.Available, whole.Decision.Status);
        Assert.Equal(TimeSpan.FromHours(2), whole.LocalStart.Offset);
        Assert.Equal(TimeSpan.FromHours(1), whole.LocalEnd.Offset);
        Assert.Equal(AvailabilityStatus.UnavailableOutsideWorkingHours, beyond.Decision.Status);
    }

    [Fact]
    public async Task A_technician_in_another_time_zone_is_evaluated_in_their_own_zone()
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database, "America/New_York", "US");
        await AddWorkingHoursAsync(technicianId, "Monday", "09:00", "17:00");

        var availability = await EvaluateAsync(technicianId, Utc(2026, 7, 6, 13), Utc(2026, 7, 6, 21));

        Assert.Equal(AvailabilityStatus.Available, availability.Decision.Status);
        Assert.Equal(new DateTimeOffset(2026, 7, 6, 9, 0, 0, TimeSpan.FromHours(-4)), availability.LocalStart);
    }
}
