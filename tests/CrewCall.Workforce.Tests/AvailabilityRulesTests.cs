using CrewCall.Workforce.Absences;
using CrewCall.Workforce.Availability;
using CrewCall.Workforce.Holidays;
using CrewCall.Workforce.WorkingHours;
using NodaTime;
using NodaTime.Text;
using Xunit;

namespace CrewCall.Workforce.Tests;

/// <summary>
/// The pure availability decision, without a database. Dates are chosen around real DST changes:
/// Europe/Warsaw springs forward on Sunday 2026-03-29 (02:00 CET → 03:00 CEST) and falls back on Sunday 2026-10-25
/// (03:00 CEST → 02:00 CET); America/New_York springs forward on Sunday 2026-03-08.
/// </summary>
public sealed class AvailabilityRulesTests
{
    private static readonly Guid _technicianId = Guid.NewGuid();
    private static readonly DateTimeZone _warsaw = DateTimeZoneProviders.Tzdb["Europe/Warsaw"];
    private static readonly DateTimeZone _newYork = DateTimeZoneProviders.Tzdb["America/New_York"];

    private static TechnicianWorkingHours Hours(DayOfWeek day, string start, string end) =>
        new(Guid.NewGuid(), _technicianId, day, TimeOnly.Parse(start), end == "24:00" ? TimeOnly.MinValue : TimeOnly.Parse(end));

    private static TechnicianAbsence Absence(string start, string end) =>
        new(Guid.NewGuid(), _technicianId, Utc(start).ToDateTimeOffset(), Utc(end).ToDateTimeOffset(), AbsenceType.Vacation, null);

    private static HolidayCalendarEntry Holiday(string date, string? regionCode = null) =>
        new(Guid.NewGuid(), DateOnly.Parse(date), "Holiday", "PL", regionCode);

    private static Instant Utc(string text) => InstantPattern.ExtendedIso.Parse(text).Value;

    private static AvailabilityStatus Evaluate(
        DateTimeZone zone,
        string start,
        string end,
        IEnumerable<TechnicianWorkingHours> workingHours,
        IEnumerable<HolidayCalendarEntry>? holidays = null,
        IEnumerable<TechnicianAbsence>? absences = null,
        bool isActive = true) =>
        AvailabilityRules.Evaluate(isActive, zone, Utc(start), Utc(end), holidays ?? [], absences ?? [], workingHours).Status;

    // Monday 2026-07-06, Warsaw on summer time (UTC+2): 08:00–16:00 local is 06:00Z–14:00Z.
    private static readonly TechnicianWorkingHours[] _mondayDayShift = [Hours(DayOfWeek.Monday, "08:00", "16:00")];

    [Theory]
    [InlineData("2026-07-06T07:00:00Z", "2026-07-06T13:00:00Z", AvailabilityStatus.Available)]                      // inside
    [InlineData("2026-07-06T06:00:00Z", "2026-07-06T14:00:00Z", AvailabilityStatus.Available)]                      // exactly the range
    [InlineData("2026-07-06T15:00:00Z", "2026-07-06T16:00:00Z", AvailabilityStatus.UnavailableOutsideWorkingHours)] // after
    [InlineData("2026-07-06T04:00:00Z", "2026-07-06T05:00:00Z", AvailabilityStatus.UnavailableOutsideWorkingHours)] // before
    [InlineData("2026-07-06T13:00:00Z", "2026-07-06T15:00:00Z", AvailabilityStatus.UnavailableOutsideWorkingHours)] // partly after
    [InlineData("2026-07-06T05:59:00Z", "2026-07-06T07:00:00Z", AvailabilityStatus.UnavailableOutsideWorkingHours)] // partly before
    [InlineData("2026-07-07T07:00:00Z", "2026-07-07T08:00:00Z", AvailabilityStatus.UnavailableOutsideWorkingHours)] // other weekday
    public void Working_hours_must_cover_the_whole_interval(string start, string end, AvailabilityStatus expected) =>
        Assert.Equal(expected, Evaluate(_warsaw, start, end, _mondayDayShift));

    [Fact]
    public void Local_working_hours_follow_the_zone_offset_of_each_date()
    {
        // Monday 2026-01-05, Warsaw on standard time (UTC+1): 08:00–16:00 local is 07:00Z–15:00Z.
        Assert.Equal(AvailabilityStatus.Available, Evaluate(_warsaw, "2026-01-05T07:00:00Z", "2026-01-05T15:00:00Z", _mondayDayShift));
        Assert.Equal(AvailabilityStatus.UnavailableOutsideWorkingHours,
            Evaluate(_warsaw, "2026-01-05T06:00:00Z", "2026-01-05T14:00:00Z", _mondayDayShift));
    }

    [Fact]
    public void A_split_shift_does_not_cover_its_break()
    {
        TechnicianWorkingHours[] split = [Hours(DayOfWeek.Monday, "08:00", "12:00"), Hours(DayOfWeek.Monday, "13:00", "17:00")];

        Assert.Equal(AvailabilityStatus.Available, Evaluate(_warsaw, "2026-07-06T11:00:00Z", "2026-07-06T15:00:00Z", split));
        Assert.Equal(AvailabilityStatus.UnavailableOutsideWorkingHours,
            Evaluate(_warsaw, "2026-07-06T09:00:00Z", "2026-07-06T12:00:00Z", split));
    }

    [Fact]
    public void Touching_ranges_join_into_one_period()
    {
        TechnicianWorkingHours[] touching = [Hours(DayOfWeek.Monday, "08:00", "12:00"), Hours(DayOfWeek.Monday, "12:00", "16:00")];

        Assert.Equal(AvailabilityStatus.Available, Evaluate(_warsaw, "2026-07-06T06:00:00Z", "2026-07-06T14:00:00Z", touching));
    }

    [Fact]
    public void A_night_shift_across_midnight_is_two_touching_ranges()
    {
        // Monday 22:00–24:00 and Tuesday 00:00–06:00 local: 2026-07-06T20:00Z to 2026-07-07T04:00Z.
        TechnicianWorkingHours[] night = [Hours(DayOfWeek.Monday, "22:00", "24:00"), Hours(DayOfWeek.Tuesday, "00:00", "06:00")];

        Assert.Equal(AvailabilityStatus.Available, Evaluate(_warsaw, "2026-07-06T21:00:00Z", "2026-07-07T02:00:00Z", night));
        Assert.Equal(AvailabilityStatus.Available, Evaluate(_warsaw, "2026-07-06T20:00:00Z", "2026-07-07T04:00:00Z", night));

        // Without the Tuesday range the interval is not covered after midnight.
        Assert.Equal(AvailabilityStatus.UnavailableOutsideWorkingHours,
            Evaluate(_warsaw, "2026-07-06T21:00:00Z", "2026-07-07T02:00:00Z", night[..1]));

        // A gap before midnight breaks the period.
        TechnicianWorkingHours[] gap = [Hours(DayOfWeek.Monday, "22:00", "23:30"), Hours(DayOfWeek.Tuesday, "00:00", "06:00")];
        Assert.Equal(AvailabilityStatus.UnavailableOutsideWorkingHours,
            Evaluate(_warsaw, "2026-07-06T21:00:00Z", "2026-07-07T02:00:00Z", gap));
    }

    [Fact]
    public void The_same_UTC_interval_depends_on_the_technicians_time_zone()
    {
        // 13:00Z–21:00Z on Monday 2026-07-06 is 09:00–17:00 in New York (UTC-4) but 15:00–23:00 in Warsaw (UTC+2).
        TechnicianWorkingHours[] nineToFive = [Hours(DayOfWeek.Monday, "09:00", "17:00")];

        Assert.Equal(AvailabilityStatus.Available, Evaluate(_newYork, "2026-07-06T13:00:00Z", "2026-07-06T21:00:00Z", nineToFive));
        Assert.Equal(AvailabilityStatus.UnavailableOutsideWorkingHours,
            Evaluate(_warsaw, "2026-07-06T13:00:00Z", "2026-07-06T21:00:00Z", nineToFive));
    }

    [Fact]
    public void New_York_working_hours_move_in_UTC_when_US_DST_starts()
    {
        TechnicianWorkingHours[] nineToFive = [Hours(DayOfWeek.Monday, "09:00", "17:00")];

        // Monday 2026-03-02 (EST, UTC-5): 14:00Z–22:00Z. Monday 2026-03-09 (EDT, UTC-4): 13:00Z–21:00Z.
        Assert.Equal(AvailabilityStatus.Available, Evaluate(_newYork, "2026-03-02T14:00:00Z", "2026-03-02T22:00:00Z", nineToFive));
        Assert.Equal(AvailabilityStatus.UnavailableOutsideWorkingHours,
            Evaluate(_newYork, "2026-03-09T14:00:00Z", "2026-03-09T22:00:00Z", nineToFive));
        Assert.Equal(AvailabilityStatus.Available, Evaluate(_newYork, "2026-03-09T13:00:00Z", "2026-03-09T21:00:00Z", nineToFive));
    }

    // --- DST spring forward: Warsaw, Sunday 2026-03-29, 02:00 CET (UTC+1) → 03:00 CEST (UTC+2) ---

    [Fact]
    public void Spring_forward_moves_the_same_local_hours_one_hour_earlier_in_UTC()
    {
        TechnicianWorkingHours[] weekend = [Hours(DayOfWeek.Saturday, "08:00", "16:00"), Hours(DayOfWeek.Sunday, "08:00", "16:00")];

        // Saturday (CET): 07:00Z–15:00Z.
        Assert.Equal(AvailabilityStatus.Available, Evaluate(_warsaw, "2026-03-28T07:00:00Z", "2026-03-28T15:00:00Z", weekend));

        // Sunday (CEST): 06:00Z–14:00Z. Saturday's UTC hours would run past the end of the shift.
        Assert.Equal(AvailabilityStatus.Available, Evaluate(_warsaw, "2026-03-29T06:00:00Z", "2026-03-29T14:00:00Z", weekend));
        Assert.Equal(AvailabilityStatus.UnavailableOutsideWorkingHours,
            Evaluate(_warsaw, "2026-03-29T07:00:00Z", "2026-03-29T15:00:00Z", weekend));
    }

    [Fact]
    public void Spring_forward_night_shift_is_one_hour_shorter_in_real_time()
    {
        // Saturday 22:00 CET (21:00Z) to Sunday 06:00 CEST (04:00Z): 8 wall-clock hours, 7 real hours.
        TechnicianWorkingHours[] night = [Hours(DayOfWeek.Saturday, "22:00", "24:00"), Hours(DayOfWeek.Sunday, "00:00", "06:00")];

        Assert.Equal(AvailabilityStatus.Available, Evaluate(_warsaw, "2026-03-28T21:00:00Z", "2026-03-29T04:00:00Z", night));
        Assert.Equal(AvailabilityStatus.UnavailableOutsideWorkingHours,
            Evaluate(_warsaw, "2026-03-28T21:00:00Z", "2026-03-29T04:30:00Z", night));

        var period = Assert.Single(AvailabilityRules.WorkingPeriods(_warsaw, new LocalDate(2026, 3, 28), new LocalDate(2026, 3, 29), night));
        Assert.Equal(Duration.FromHours(7), period.Duration);
    }

    [Fact]
    public void A_range_starting_in_the_spring_forward_gap_starts_when_the_clock_jumps_past_it()
    {
        // 02:30 does not exist on 2026-03-29 in Warsaw; it resolves to 03:00 CEST = 01:00Z.
        TechnicianWorkingHours[] early = [Hours(DayOfWeek.Sunday, "02:30", "06:00")];

        Assert.Equal(AvailabilityStatus.Available, Evaluate(_warsaw, "2026-03-29T01:00:00Z", "2026-03-29T04:00:00Z", early));
        Assert.Equal(AvailabilityStatus.UnavailableOutsideWorkingHours,
            Evaluate(_warsaw, "2026-03-29T00:45:00Z", "2026-03-29T04:00:00Z", early));
    }

    [Fact]
    public void Ranges_touching_inside_the_spring_forward_gap_still_touch()
    {
        // Both 02:30 ends resolve to the same instant (01:00Z), so the two ranges remain one period.
        TechnicianWorkingHours[] split = [Hours(DayOfWeek.Sunday, "00:00", "02:30"), Hours(DayOfWeek.Sunday, "02:30", "06:00")];

        Assert.Equal(AvailabilityStatus.Available, Evaluate(_warsaw, "2026-03-28T23:00:00Z", "2026-03-29T04:00:00Z", split));
    }

    [Fact]
    public void A_range_entirely_inside_the_spring_forward_gap_has_no_working_time()
    {
        TechnicianWorkingHours[] skipped = [Hours(DayOfWeek.Sunday, "02:15", "02:45")];

        Assert.Empty(AvailabilityRules.WorkingPeriods(_warsaw, new LocalDate(2026, 3, 29), new LocalDate(2026, 3, 29), skipped));
    }

    // --- DST fall back: Warsaw, Sunday 2026-10-25, 03:00 CEST (UTC+2) → 02:00 CET (UTC+1) ---

    [Fact]
    public void Fall_back_night_shift_is_one_hour_longer_in_real_time()
    {
        // Sunday 00:00 CEST (Saturday 22:00Z) to 06:00 CET (05:00Z): 6 wall-clock hours, 7 real hours.
        TechnicianWorkingHours[] night = [Hours(DayOfWeek.Sunday, "00:00", "06:00")];

        Assert.Equal(AvailabilityStatus.Available, Evaluate(_warsaw, "2026-10-24T22:00:00Z", "2026-10-25T05:00:00Z", night));
        Assert.Equal(AvailabilityStatus.UnavailableOutsideWorkingHours,
            Evaluate(_warsaw, "2026-10-24T22:00:00Z", "2026-10-25T05:30:00Z", night));

        var period = Assert.Single(AvailabilityRules.WorkingPeriods(_warsaw, new LocalDate(2026, 10, 25), new LocalDate(2026, 10, 25), night));
        Assert.Equal(Duration.FromHours(7), period.Duration);
    }

    [Fact]
    public void An_ambiguous_fall_back_time_resolves_to_its_first_occurrence()
    {
        // 02:30 occurs twice on 2026-10-25 in Warsaw: 00:30Z (CEST) and 01:30Z (CET). The earlier one is used.
        TechnicianWorkingHours[] early = [Hours(DayOfWeek.Sunday, "02:30", "06:00")];

        Assert.Equal(AvailabilityStatus.Available, Evaluate(_warsaw, "2026-10-25T00:30:00Z", "2026-10-25T05:00:00Z", early));
        Assert.Equal(AvailabilityStatus.UnavailableOutsideWorkingHours,
            Evaluate(_warsaw, "2026-10-25T00:00:00Z", "2026-10-25T05:00:00Z", early));
    }

    [Fact]
    public void After_fall_back_the_same_local_hours_are_one_hour_later_in_UTC()
    {
        TechnicianWorkingHours[] weekend = [Hours(DayOfWeek.Saturday, "08:00", "16:00"), Hours(DayOfWeek.Sunday, "08:00", "16:00")];

        Assert.Equal(AvailabilityStatus.Available, Evaluate(_warsaw, "2026-10-24T06:00:00Z", "2026-10-24T14:00:00Z", weekend));
        Assert.Equal(AvailabilityStatus.Available, Evaluate(_warsaw, "2026-10-25T07:00:00Z", "2026-10-25T15:00:00Z", weekend));
        Assert.Equal(AvailabilityStatus.UnavailableOutsideWorkingHours,
            Evaluate(_warsaw, "2026-10-25T06:00:00Z", "2026-10-25T14:00:00Z", weekend));
    }

    // --- Holidays, absences and the order of checks ---

    private static readonly TechnicianWorkingHours[] _allWeek =
        Enum.GetValues<DayOfWeek>().Select(day => Hours(day, "00:00", "24:00")).ToArray();

    [Fact]
    public void A_national_holiday_on_any_local_date_of_the_interval_makes_it_unavailable()
    {
        Assert.Equal(AvailabilityStatus.UnavailableHoliday,
            Evaluate(_warsaw, "2026-07-06T10:00:00Z", "2026-07-06T11:00:00Z", _allWeek, [Holiday("2026-07-06")]));

        // Spans two local dates; the holiday is the second.
        Assert.Equal(AvailabilityStatus.UnavailableHoliday,
            Evaluate(_warsaw, "2026-07-06T20:00:00Z", "2026-07-06T23:00:00Z", _allWeek, [Holiday("2026-07-07")]));
    }

    [Fact]
    public void Holidays_are_local_dates_not_UTC_dates()
    {
        // 22:30Z on 2026-07-06 is already 00:30 on 2026-07-07 in Warsaw.
        Assert.Equal(AvailabilityStatus.UnavailableHoliday,
            Evaluate(_warsaw, "2026-07-06T22:30:00Z", "2026-07-06T23:30:00Z", _allWeek, [Holiday("2026-07-07")]));

        // 02:00Z on 2026-07-07 is still 22:00 on 2026-07-06 in New York.
        Assert.Equal(AvailabilityStatus.Available,
            Evaluate(_newYork, "2026-07-07T02:00:00Z", "2026-07-07T03:00:00Z", _allWeek, [Holiday("2026-07-07")]));
    }

    [Fact]
    public void An_interval_ending_at_the_holidays_local_midnight_does_not_touch_it()
    {
        // 20:00Z–22:00Z is 22:00–24:00 on 2026-07-06 in Warsaw: the end is exclusive.
        Assert.Equal(AvailabilityStatus.Available,
            Evaluate(_warsaw, "2026-07-06T20:00:00Z", "2026-07-06T22:00:00Z", _allWeek, [Holiday("2026-07-07")]));
    }

    [Fact]
    public void Regional_holidays_do_not_apply_yet()
    {
        Assert.Equal(AvailabilityStatus.Available,
            Evaluate(_warsaw, "2026-07-06T10:00:00Z", "2026-07-06T11:00:00Z", _allWeek, [Holiday("2026-07-06", "MZ")]));
    }

    [Theory]
    [InlineData("2026-07-06T09:00:00Z", "2026-07-06T11:00:00Z", AvailabilityStatus.UnavailableAbsence)] // overlaps the start
    [InlineData("2026-07-06T11:00:00Z", "2026-07-06T13:00:00Z", AvailabilityStatus.UnavailableAbsence)] // overlaps the end
    [InlineData("2026-07-06T10:30:00Z", "2026-07-06T11:30:00Z", AvailabilityStatus.UnavailableAbsence)] // inside
    [InlineData("2026-07-06T08:00:00Z", "2026-07-06T10:00:00Z", AvailabilityStatus.Available)]          // ends when the interval starts
    [InlineData("2026-07-06T12:00:00Z", "2026-07-06T14:00:00Z", AvailabilityStatus.Available)]          // starts when the interval ends
    public void An_overlapping_absence_makes_the_interval_unavailable_but_a_touching_one_does_not(
        string absenceStart, string absenceEnd, AvailabilityStatus expected) =>
        Assert.Equal(expected,
            Evaluate(_warsaw, "2026-07-06T10:00:00Z", "2026-07-06T12:00:00Z", _allWeek, absences: [Absence(absenceStart, absenceEnd)]));

    [Fact]
    public void Checks_run_in_order_inactive_holiday_absence_working_hours()
    {
        const string start = "2026-07-06T20:00:00Z";
        const string end = "2026-07-06T21:00:00Z"; // 22:00–23:00 local: outside the Monday day shift
        HolidayCalendarEntry[] holiday = [Holiday("2026-07-06")];
        TechnicianAbsence[] absence = [Absence("2026-07-06T00:00:00Z", "2026-07-07T00:00:00Z")];

        Assert.Equal(AvailabilityStatus.UnavailableInactiveTechnician,
            Evaluate(_warsaw, start, end, _mondayDayShift, holiday, absence, isActive: false));
        Assert.Equal(AvailabilityStatus.UnavailableHoliday, Evaluate(_warsaw, start, end, _mondayDayShift, holiday, absence));
        Assert.Equal(AvailabilityStatus.UnavailableAbsence, Evaluate(_warsaw, start, end, _mondayDayShift, absences: absence));
        Assert.Equal(AvailabilityStatus.UnavailableOutsideWorkingHours, Evaluate(_warsaw, start, end, _mondayDayShift));
    }

    [Fact]
    public void Without_working_hours_a_technician_is_never_available()
    {
        Assert.Equal(AvailabilityStatus.UnavailableOutsideWorkingHours,
            Evaluate(_warsaw, "2026-07-06T10:00:00Z", "2026-07-06T11:00:00Z", []));
    }

    [Fact]
    public void An_empty_interval_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            Evaluate(_warsaw, "2026-07-06T10:00:00Z", "2026-07-06T10:00:00Z", _allWeek));
    }
}
