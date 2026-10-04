using System.Net;
using System.Net.Http.Json;
using CrewCall.Contracts.Absences;
using CrewCall.Contracts.Availability;
using CrewCall.Contracts.Holidays;
using CrewCall.Contracts.WorkingHours;
using Xunit;

namespace CrewCall.Api.Tests;

public sealed class WorkforceAvailabilityEndpointsTests(CrewCallApiFactory factory)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static async Task AddWorkingHoursAsync(HttpClient client, Guid technicianId, string day, string start, string end)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/technicians/{technicianId}/working-hours", new CreateWorkingHoursRequest(day, start, end), Cancellation);
        response.EnsureSuccessStatusCode();
    }

    private static string AvailabilityUrl(Guid technicianId, string start, string end) =>
        $"/api/technicians/{technicianId}/availability?start={Uri.EscapeDataString(start)}&end={Uri.EscapeDataString(end)}";

    private static async Task<TechnicianAvailabilityResponse> GetAvailabilityAsync(HttpClient client, Guid technicianId, string start, string end)
    {
        using var response = await client.GetAsync(AvailabilityUrl(technicianId, start, end), Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TechnicianAvailabilityResponse>(Cancellation))!;
    }

    // --- Working hours ---

    [Fact]
    public async Task Post_working_hours_returns_201_and_get_lists_them()
    {
        using var client = factory.CreateClient();
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);

        using var response = await client.PostAsJsonAsync(
            $"/api/technicians/{technicianId}/working-hours", new CreateWorkingHoursRequest("Friday", "22:00", "24:00"), Cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<WorkingHoursResponse>(Cancellation);
        Assert.NotNull(created);
        Assert.Equal(new WorkingHoursResponse(created.Id, technicianId, "Friday", "22:00", "24:00"), created);
        Assert.Equal($"/api/technicians/{technicianId}/working-hours/{created.Id}", response.Headers.Location?.OriginalString);

        var list = await client.GetFromJsonAsync<WorkingHoursResponse[]>($"/api/technicians/{technicianId}/working-hours", Cancellation);
        Assert.NotNull(list);
        Assert.Equal([created], list);
    }

    [Fact]
    public async Task Post_overlapping_working_hours_returns_409()
    {
        using var client = factory.CreateClient();
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);
        await AddWorkingHoursAsync(client, technicianId, "Monday", "08:00", "16:00");

        using var response = await client.PostAsJsonAsync(
            $"/api/technicians/{technicianId}/working-hours", new CreateWorkingHoursRequest("Monday", "15:00", "18:00"), Cancellation);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Post_invalid_working_hours_returns_400()
    {
        using var client = factory.CreateClient();
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);

        using var response = await client.PostAsJsonAsync(
            $"/api/technicians/{technicianId}/working-hours", new CreateWorkingHoursRequest("Monday", "16:00", "08:00"), Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Working_hours_of_a_missing_technician_return_404()
    {
        using var client = factory.CreateClient();
        var missing = Guid.NewGuid();

        using var post = await client.PostAsJsonAsync(
            $"/api/technicians/{missing}/working-hours", new CreateWorkingHoursRequest("Monday", "08:00", "16:00"), Cancellation);
        using var get = await client.GetAsync($"/api/technicians/{missing}/working-hours", Cancellation);
        using var delete = await client.DeleteAsync($"/api/technicians/{missing}/working-hours/{Guid.NewGuid()}", Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task Delete_working_hours_returns_204_and_removes_them()
    {
        using var client = factory.CreateClient();
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);
        using var post = await client.PostAsJsonAsync(
            $"/api/technicians/{technicianId}/working-hours", new CreateWorkingHoursRequest("Monday", "08:00", "16:00"), Cancellation);
        var created = await post.Content.ReadFromJsonAsync<WorkingHoursResponse>(Cancellation);

        using var delete = await client.DeleteAsync($"/api/technicians/{technicianId}/working-hours/{created!.Id}", Cancellation);
        using var again = await client.DeleteAsync($"/api/technicians/{technicianId}/working-hours/{created.Id}", Cancellation);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<WorkingHoursResponse[]>($"/api/technicians/{technicianId}/working-hours", Cancellation))!);
    }

    // --- Absences ---

    [Fact]
    public async Task Post_absence_returns_201_in_UTC_and_get_lists_it()
    {
        using var client = factory.CreateClient();
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);
        var request = new CreateAbsenceRequest(
            new DateTimeOffset(2026, 8, 3, 8, 0, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2026, 8, 7, 18, 0, 0, TimeSpan.FromHours(2)),
            "Vacation",
            "Summer");

        using var response = await client.PostAsJsonAsync($"/api/technicians/{technicianId}/absences", request, Cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AbsenceResponse>(Cancellation);
        Assert.NotNull(created);
        Assert.Equal(new DateTimeOffset(2026, 8, 3, 6, 0, 0, TimeSpan.Zero), created.Start);
        Assert.Equal(TimeSpan.Zero, created.Start.Offset);
        Assert.Equal("Vacation", created.Type);

        var list = await client.GetFromJsonAsync<AbsenceResponse[]>($"/api/technicians/{technicianId}/absences", Cancellation);
        Assert.Equal(created.Id, Assert.Single(list!).Id);
    }

    [Fact]
    public async Task Post_overlapping_absence_returns_409_and_touching_absence_201()
    {
        using var client = factory.CreateClient();
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);
        var day = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        (await client.PostAsJsonAsync($"/api/technicians/{technicianId}/absences",
            new CreateAbsenceRequest(day.AddHours(8), day.AddHours(12), "Training", null), Cancellation)).EnsureSuccessStatusCode();

        using var overlapping = await client.PostAsJsonAsync($"/api/technicians/{technicianId}/absences",
            new CreateAbsenceRequest(day.AddHours(11), day.AddHours(13), "SickLeave", null), Cancellation);
        using var touching = await client.PostAsJsonAsync($"/api/technicians/{technicianId}/absences",
            new CreateAbsenceRequest(day.AddHours(12), day.AddHours(13), "SickLeave", null), Cancellation);

        Assert.Equal(HttpStatusCode.Conflict, overlapping.StatusCode);
        Assert.Equal(HttpStatusCode.Created, touching.StatusCode);
    }

    [Fact]
    public async Task Post_invalid_absence_returns_400_and_missing_technician_404()
    {
        using var client = factory.CreateClient();
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);
        var day = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);

        using var invalid = await client.PostAsJsonAsync($"/api/technicians/{technicianId}/absences",
            new CreateAbsenceRequest(day.AddHours(12), day.AddHours(8), "Holiday", null), Cancellation);
        using var missing = await client.PostAsJsonAsync($"/api/technicians/{Guid.NewGuid()}/absences",
            new CreateAbsenceRequest(day.AddHours(8), day.AddHours(12), "Other", null), Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Delete_absence_returns_204()
    {
        using var client = factory.CreateClient();
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);
        var day = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        using var post = await client.PostAsJsonAsync($"/api/technicians/{technicianId}/absences",
            new CreateAbsenceRequest(day, day.AddDays(1), "Other", null), Cancellation);
        var created = await post.Content.ReadFromJsonAsync<AbsenceResponse>(Cancellation);

        using var delete = await client.DeleteAsync($"/api/technicians/{technicianId}/absences/{created!.Id}", Cancellation);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<AbsenceResponse[]>($"/api/technicians/{technicianId}/absences", Cancellation))!);
    }

    // --- Holidays ---

    [Fact]
    public async Task Post_holiday_returns_201_get_filters_by_country_and_a_duplicate_returns_409()
    {
        using var client = factory.CreateClient();
        var date = ApiTestData.UniqueHolidayDate();

        using var created = await client.PostAsJsonAsync("/api/holidays", new CreateHolidayRequest(date, "Testowe święto", "pl", null), Cancellation);
        using var duplicate = await client.PostAsJsonAsync("/api/holidays", new CreateHolidayRequest(date, "Again", "PL", null), Cancellation);
        (await client.PostAsJsonAsync("/api/holidays", new CreateHolidayRequest(date, "Other", "DE", null), Cancellation)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var holiday = await created.Content.ReadFromJsonAsync<HolidayResponse>(Cancellation);
        Assert.Equal(new HolidayResponse(holiday!.Id, date, "Testowe święto", "PL", null), holiday);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var polish = await client.GetFromJsonAsync<HolidayResponse[]>("/api/holidays?countryCode=PL", Cancellation);
        Assert.Contains(holiday, polish!);
        Assert.All(polish!, entry => Assert.Equal("PL", entry.CountryCode));
    }

    [Fact]
    public async Task Holiday_validation_errors_return_400()
    {
        using var client = factory.CreateClient();

        using var post = await client.PostAsJsonAsync(
            "/api/holidays", new CreateHolidayRequest(ApiTestData.UniqueHolidayDate(), "Name", "XX", null), Cancellation);
        using var get = await client.GetAsync("/api/holidays?countryCode=POL", Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, get.StatusCode);
    }

    // --- Availability ---

    [Fact]
    public async Task Availability_inside_working_hours_returns_available_with_local_times()
    {
        using var client = factory.CreateClient();
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);
        await AddWorkingHoursAsync(client, technicianId, "Monday", "08:00", "16:00");

        var availability = await GetAvailabilityAsync(client, technicianId, "2026-07-06T06:00:00Z", "2026-07-06T14:00:00Z");

        Assert.True(availability.IsAvailable);
        Assert.Equal("Available", availability.Reason);
        Assert.Null(availability.Detail);
        Assert.Equal("Europe/Warsaw", availability.TimeZoneId);
        Assert.Equal(new DateTimeOffset(2026, 7, 6, 8, 0, 0, TimeSpan.FromHours(2)), availability.LocalStart);
        Assert.Equal(TimeSpan.FromHours(2), availability.LocalStart.Offset);
    }

    [Fact]
    public async Task Availability_accepts_an_explicit_offset_including_an_unencoded_plus()
    {
        using var client = factory.CreateClient();
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);
        await AddWorkingHoursAsync(client, technicianId, "Monday", "08:00", "16:00");

        var encoded = await GetAvailabilityAsync(client, technicianId, "2026-07-06T08:00:00+02:00", "2026-07-06T16:00:00+02:00");
        var unencoded = await client.GetFromJsonAsync<TechnicianAvailabilityResponse>(
            $"/api/technicians/{technicianId}/availability?start=2026-07-06T08:00+02:00&end=2026-07-06T16:00+02:00", Cancellation);

        Assert.True(encoded.IsAvailable);
        Assert.Equal(new DateTimeOffset(2026, 7, 6, 6, 0, 0, TimeSpan.Zero), encoded.Start);
        Assert.True(unencoded!.IsAvailable);
    }

    [Fact]
    public async Task Availability_reports_each_unavailability_reason()
    {
        using var client = factory.CreateClient();
        var date = ApiTestData.UniqueHolidayDate();
        var holidayStart = new DateTimeOffset(date.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero).ToString("O");
        var holidayEnd = new DateTimeOffset(date.ToDateTime(new TimeOnly(11, 0)), TimeSpan.Zero).ToString("O");

        var inactiveId = await ApiTestData.CreateTechnicianAsync(client, isActive: false);
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);
        foreach (var day in Enum.GetNames<DayOfWeek>())
        {
            await AddWorkingHoursAsync(client, technicianId, day, "06:00", "18:00");
        }

        (await client.PostAsJsonAsync("/api/holidays", new CreateHolidayRequest(date, "Holiday", "PL", null), Cancellation)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/technicians/{technicianId}/absences", new CreateAbsenceRequest(
            new DateTimeOffset(2026, 7, 6, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 7, 6, 10, 0, 0, TimeSpan.Zero), "SickLeave", null),
            Cancellation)).EnsureSuccessStatusCode();

        var inactive = await GetAvailabilityAsync(client, inactiveId, "2026-07-06T08:00:00Z", "2026-07-06T09:00:00Z");
        var holiday = await GetAvailabilityAsync(client, technicianId, holidayStart, holidayEnd);
        var absence = await GetAvailabilityAsync(client, technicianId, "2026-07-06T09:00:00Z", "2026-07-06T11:00:00Z");
        var touchingAbsence = await GetAvailabilityAsync(client, technicianId, "2026-07-06T10:00:00Z", "2026-07-06T11:00:00Z");
        var outside = await GetAvailabilityAsync(client, technicianId, "2026-07-06T15:00:00Z", "2026-07-06T17:00:00Z");

        Assert.Equal("UnavailableInactiveTechnician", inactive.Reason);
        Assert.Equal("UnavailableHoliday", holiday.Reason);
        Assert.Equal("UnavailableAbsence", absence.Reason);
        Assert.Equal("SickLeave", absence.Detail);
        Assert.Equal("Available", touchingAbsence.Reason);
        Assert.Equal("UnavailableOutsideWorkingHours", outside.Reason);
        Assert.All([inactive, holiday, absence, outside], result => Assert.False(result.IsAvailable));
    }

    [Fact]
    public async Task Availability_applies_DST_on_the_day_of_the_change()
    {
        using var client = factory.CreateClient();
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);
        await AddWorkingHoursAsync(client, technicianId, "Sunday", "08:00", "16:00");

        // Europe/Warsaw springs forward on 2026-03-29: 08:00–16:00 local is 06:00Z–14:00Z that day.
        var springForward = await GetAvailabilityAsync(client, technicianId, "2026-03-29T06:00:00Z", "2026-03-29T14:00:00Z");
        var oldOffset = await GetAvailabilityAsync(client, technicianId, "2026-03-29T07:00:00Z", "2026-03-29T15:00:00Z");

        // ...and falls back on 2026-10-25: 08:00–16:00 local is 07:00Z–15:00Z that day.
        var fallBack = await GetAvailabilityAsync(client, technicianId, "2026-10-25T07:00:00Z", "2026-10-25T15:00:00Z");

        Assert.True(springForward.IsAvailable);
        Assert.Equal("UnavailableOutsideWorkingHours", oldOffset.Reason);
        Assert.True(fallBack.IsAvailable);
        Assert.Equal(TimeSpan.FromHours(1), fallBack.LocalStart.Offset);
    }

    [Theory]
    [InlineData(null, "2026-07-06T09:00:00Z")]
    [InlineData("2026-07-06T08:00:00Z", null)]
    [InlineData("2026-07-06T08:00:00", "2026-07-06T09:00:00Z")] // no offset
    [InlineData("tomorrow", "2026-07-06T09:00:00Z")]
    [InlineData("2026-07-06T09:00:00Z", "2026-07-06T08:00:00Z")] // end before start
    public async Task Availability_with_invalid_query_returns_400(string? start, string? end)
    {
        using var client = factory.CreateClient();
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);
        var query = string.Join("&", new[] { ("start", start), ("end", end) }
            .Where(pair => pair.Item2 is not null)
            .Select(pair => $"{pair.Item1}={Uri.EscapeDataString(pair.Item2!)}"));

        using var response = await client.GetAsync($"/api/technicians/{technicianId}/availability?{query}", Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Availability_of_a_missing_technician_returns_404()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            AvailabilityUrl(Guid.NewGuid(), "2026-07-06T08:00:00Z", "2026-07-06T09:00:00Z"), Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
