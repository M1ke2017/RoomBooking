using CrewCall.Workforce.WorkingHours;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Workforce.Tests;

public sealed class WorkingHoursServiceTests(WorkforceDatabase database)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private async Task<CreateWorkingHoursOutcome> CreateAsync(Guid technicianId, string? day, string? start, string? end)
    {
        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkingHoursService>()
            .CreateAsync(new CreateWorkingHours(technicianId, day, start, end), Cancellation);
    }

    [Fact]
    public async Task Create_saves_a_valid_range_and_list_returns_it()
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);

        var created = Assert.IsType<CreateWorkingHoursOutcome.Created>(await CreateAsync(technicianId, "monday", "08:00", "16:30"));

        Assert.Equal(DayOfWeek.Monday, created.WorkingHours.DayOfWeek);
        Assert.Equal(new TimeOnly(8, 0), created.WorkingHours.StartLocalTime);
        Assert.Equal(new TimeOnly(16, 30), created.WorkingHours.EndLocalTime);

        await using var scope = database.CreateScope();
        var list = await scope.ServiceProvider.GetRequiredService<WorkingHoursService>().ListAsync(technicianId, Cancellation);
        Assert.NotNull(list);
        var saved = Assert.Single(list);
        Assert.Equal(created.WorkingHours.Id, saved.Id);
        Assert.Equal(new TimeOnly(16, 30), saved.EndLocalTime);
    }

    [Fact]
    public async Task A_day_can_have_several_ranges_and_list_orders_them_Monday_first()
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);
        Assert.IsType<CreateWorkingHoursOutcome.Created>(await CreateAsync(technicianId, "Sunday", "10:00", "14:00"));
        Assert.IsType<CreateWorkingHoursOutcome.Created>(await CreateAsync(technicianId, "Monday", "13:00", "17:00"));
        Assert.IsType<CreateWorkingHoursOutcome.Created>(await CreateAsync(technicianId, "Monday", "07:00", "11:00"));

        await using var scope = database.CreateScope();
        var list = await scope.ServiceProvider.GetRequiredService<WorkingHoursService>().ListAsync(technicianId, Cancellation);

        Assert.NotNull(list);
        Assert.Equal(
            [(DayOfWeek.Monday, new TimeOnly(7, 0)), (DayOfWeek.Monday, new TimeOnly(13, 0)), (DayOfWeek.Sunday, new TimeOnly(10, 0))],
            list.Select(range => (range.DayOfWeek, range.StartLocalTime)));
    }

    [Theory]
    [InlineData("08:00", "16:00", "15:00", "18:00")] // overlaps the end
    [InlineData("08:00", "16:00", "06:00", "09:00")] // overlaps the start
    [InlineData("08:00", "16:00", "10:00", "12:00")] // inside
    [InlineData("08:00", "16:00", "07:00", "17:00")] // contains
    [InlineData("08:00", "16:00", "08:00", "16:00")] // identical
    [InlineData("20:00", "24:00", "23:00", "23:30")] // inside a range that runs to midnight
    public async Task Create_rejects_a_range_overlapping_the_same_day_with_409_outcome(
        string existingStart, string existingEnd, string start, string end)
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);
        var existing = Assert.IsType<CreateWorkingHoursOutcome.Created>(await CreateAsync(technicianId, "Tuesday", existingStart, existingEnd));

        var overlap = Assert.IsType<CreateWorkingHoursOutcome.Overlaps>(await CreateAsync(technicianId, "Tuesday", start, end));

        Assert.Equal(existing.WorkingHours.Id, overlap.ExistingWorkingHoursId);
    }

    [Fact]
    public async Task Touching_ranges_other_days_and_other_technicians_do_not_overlap()
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);
        var otherTechnicianId = await WorkforceTestData.CreateTechnicianAsync(database);
        Assert.IsType<CreateWorkingHoursOutcome.Created>(await CreateAsync(technicianId, "Wednesday", "08:00", "12:00"));

        Assert.IsType<CreateWorkingHoursOutcome.Created>(await CreateAsync(technicianId, "Wednesday", "12:00", "16:00"));
        Assert.IsType<CreateWorkingHoursOutcome.Created>(await CreateAsync(technicianId, "Wednesday", "06:00", "08:00"));
        Assert.IsType<CreateWorkingHoursOutcome.Created>(await CreateAsync(technicianId, "Thursday", "08:00", "12:00"));
        Assert.IsType<CreateWorkingHoursOutcome.Created>(await CreateAsync(otherTechnicianId, "Wednesday", "08:00", "12:00"));
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("00:00")]
    public async Task An_end_of_24_00_or_00_00_runs_to_midnight_and_touches_the_next_day(string endOfDay)
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);

        var created = Assert.IsType<CreateWorkingHoursOutcome.Created>(await CreateAsync(technicianId, "Friday", "22:00", endOfDay));
        Assert.True(created.WorkingHours.EndsAtEndOfDay);
        Assert.Equal("24:00", LocalTimeText.FormatEnd(created.WorkingHours.EndLocalTime));

        // A night shift crossing midnight is two ranges.
        Assert.IsType<CreateWorkingHoursOutcome.Created>(await CreateAsync(technicianId, "Saturday", "00:00", "06:00"));
        Assert.IsType<CreateWorkingHoursOutcome.Created>(await CreateAsync(technicianId, "Friday", "18:00", "22:00"));
    }

    [Fact]
    public async Task A_whole_day_range_is_00_00_to_24_00()
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);

        Assert.IsType<CreateWorkingHoursOutcome.Created>(await CreateAsync(technicianId, "Sunday", "00:00", "24:00"));
        Assert.IsType<CreateWorkingHoursOutcome.Overlaps>(await CreateAsync(technicianId, "Sunday", "23:00", "23:59"));
    }

    [Theory]
    [InlineData(null, "08:00", "16:00", "dayOfWeek")]
    [InlineData("Funday", "08:00", "16:00", "dayOfWeek")]
    [InlineData("1", "08:00", "16:00", "dayOfWeek")]
    [InlineData("Monday", null, "16:00", "startLocalTime")]
    [InlineData("Monday", "8:00", "16:00", "startLocalTime")]
    [InlineData("Monday", "24:00", "16:00", "startLocalTime")]
    [InlineData("Monday", "08:00:30", "16:00", "startLocalTime")]
    [InlineData("Monday", "08:00", "25:00", "endLocalTime")]
    [InlineData("Monday", "16:00", "08:00", "endLocalTime")] // crosses midnight: must be split
    [InlineData("Monday", "08:00", "08:00", "endLocalTime")] // empty
    public async Task Create_rejects_invalid_input(string? day, string? start, string? end, string field)
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);

        var invalid = Assert.IsType<CreateWorkingHoursOutcome.Invalid>(await CreateAsync(technicianId, day, start, end));

        Assert.Equal([field], invalid.Errors.Keys);
    }

    [Fact]
    public async Task Operations_on_an_unknown_technician_report_not_found()
    {
        var unknown = Guid.NewGuid();

        Assert.IsType<CreateWorkingHoursOutcome.TechnicianNotFound>(await CreateAsync(unknown, "Monday", "08:00", "16:00"));

        await using var scope = database.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkingHoursService>();
        Assert.Null(await service.ListAsync(unknown, Cancellation));
        Assert.IsType<DeleteWorkingHoursOutcome.TechnicianNotFound>(await service.DeleteAsync(unknown, Guid.NewGuid(), Cancellation));
    }

    [Fact]
    public async Task Delete_removes_the_range_and_is_idempotent()
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);
        var created = Assert.IsType<CreateWorkingHoursOutcome.Created>(await CreateAsync(technicianId, "Monday", "08:00", "16:00"));

        await using var scope = database.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkingHoursService>();
        Assert.IsType<DeleteWorkingHoursOutcome.Deleted>(await service.DeleteAsync(technicianId, created.WorkingHours.Id, Cancellation));
        Assert.IsType<DeleteWorkingHoursOutcome.Deleted>(await service.DeleteAsync(technicianId, created.WorkingHours.Id, Cancellation));

        Assert.Empty((await service.ListAsync(technicianId, Cancellation))!);
    }

    [Fact]
    public async Task Delete_does_not_remove_another_technicians_range()
    {
        var ownerId = await WorkforceTestData.CreateTechnicianAsync(database);
        var otherId = await WorkforceTestData.CreateTechnicianAsync(database);
        var created = Assert.IsType<CreateWorkingHoursOutcome.Created>(await CreateAsync(ownerId, "Monday", "08:00", "16:00"));

        await using var scope = database.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkingHoursService>();
        Assert.IsType<DeleteWorkingHoursOutcome.Deleted>(await service.DeleteAsync(otherId, created.WorkingHours.Id, Cancellation));

        Assert.Single((await service.ListAsync(ownerId, Cancellation))!);
    }
}
