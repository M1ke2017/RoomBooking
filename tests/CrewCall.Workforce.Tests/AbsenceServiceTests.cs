using CrewCall.Workforce.Absences;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Workforce.Tests;

public sealed class AbsenceServiceTests(WorkforceDatabase database)
{
    private static readonly DateTimeOffset _day = new(2026, 7, 6, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private async Task<CreateAbsenceOutcome> CreateAsync(
        Guid technicianId, DateTimeOffset? start, DateTimeOffset? end, string? type = "Vacation", string? reason = null)
    {
        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AbsenceService>()
            .CreateAsync(new CreateAbsence(technicianId, start, end, type, reason), Cancellation);
    }

    [Fact]
    public async Task Create_saves_the_absence_in_UTC_and_list_returns_it()
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);
        var start = new DateTimeOffset(2026, 7, 6, 8, 0, 0, TimeSpan.FromHours(2));
        var end = new DateTimeOffset(2026, 7, 10, 18, 0, 0, TimeSpan.FromHours(2));

        var created = Assert.IsType<CreateAbsenceOutcome.Created>(await CreateAsync(technicianId, start, end, "sickleave", " Flu "));

        Assert.Equal(AbsenceType.SickLeave, created.Absence.Type);
        Assert.Equal("Flu", created.Absence.Reason);

        await using var scope = database.CreateScope();
        var saved = Assert.Single((await scope.ServiceProvider.GetRequiredService<AbsenceService>().ListAsync(technicianId, Cancellation))!);
        Assert.Equal(start, saved.Start);
        Assert.Equal(TimeSpan.Zero, saved.Start.Offset);
        Assert.Equal(end, saved.End);
    }

    [Theory]
    [InlineData(8, 12, 11, 14)] // overlaps the end
    [InlineData(8, 12, 6, 9)]   // overlaps the start
    [InlineData(8, 12, 9, 10)]  // inside
    [InlineData(8, 12, 7, 13)]  // contains
    public async Task Create_rejects_an_overlapping_absence(int existingStart, int existingEnd, int start, int end)
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);
        var existing = Assert.IsType<CreateAbsenceOutcome.Created>(
            await CreateAsync(technicianId, _day.AddHours(existingStart), _day.AddHours(existingEnd)));

        var overlap = Assert.IsType<CreateAbsenceOutcome.Overlaps>(
            await CreateAsync(technicianId, _day.AddHours(start), _day.AddHours(end), "Training"));

        Assert.Equal(existing.Absence.Id, overlap.ExistingAbsenceId);
    }

    [Fact]
    public async Task Touching_absences_and_other_technicians_absences_do_not_overlap()
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);
        var otherId = await WorkforceTestData.CreateTechnicianAsync(database);
        Assert.IsType<CreateAbsenceOutcome.Created>(await CreateAsync(technicianId, _day.AddHours(8), _day.AddHours(12)));

        Assert.IsType<CreateAbsenceOutcome.Created>(await CreateAsync(technicianId, _day.AddHours(12), _day.AddHours(16)));
        Assert.IsType<CreateAbsenceOutcome.Created>(await CreateAsync(technicianId, _day.AddHours(4), _day.AddHours(8)));
        Assert.IsType<CreateAbsenceOutcome.Created>(await CreateAsync(otherId, _day.AddHours(8), _day.AddHours(12)));
    }

    [Fact]
    public async Task Create_rejects_missing_values_and_end_not_after_start()
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);

        var missing = Assert.IsType<CreateAbsenceOutcome.Invalid>(await CreateAsync(technicianId, null, null, null));
        Assert.Equal(["end", "start", "type"], missing.Errors.Keys.Order());

        var reversed = Assert.IsType<CreateAbsenceOutcome.Invalid>(await CreateAsync(technicianId, _day.AddHours(2), _day.AddHours(1)));
        Assert.Equal(["end"], reversed.Errors.Keys);

        var empty = Assert.IsType<CreateAbsenceOutcome.Invalid>(await CreateAsync(technicianId, _day, _day));
        Assert.Equal(["end"], empty.Errors.Keys);

        // The same instant written with different offsets is still an empty interval.
        var sameInstant = Assert.IsType<CreateAbsenceOutcome.Invalid>(
            await CreateAsync(technicianId, _day.AddHours(10), _day.AddHours(12).ToOffset(TimeSpan.FromHours(2)).AddHours(-2)));
        Assert.Equal(["end"], sameInstant.Errors.Keys);
    }

    [Theory]
    [InlineData("Holiday")]
    [InlineData("0")]
    [InlineData("Vacation, Training")]
    public async Task Create_rejects_an_unknown_type(string type)
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);

        var invalid = Assert.IsType<CreateAbsenceOutcome.Invalid>(await CreateAsync(technicianId, _day, _day.AddDays(1), type));

        Assert.Equal(["type"], invalid.Errors.Keys);
    }

    [Fact]
    public async Task Create_rejects_a_too_long_reason()
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);

        var invalid = Assert.IsType<CreateAbsenceOutcome.Invalid>(
            await CreateAsync(technicianId, _day, _day.AddDays(1), "Other", new string('x', TechnicianAbsence.ReasonMaxLength + 1)));

        Assert.Equal(["reason"], invalid.Errors.Keys);
    }

    [Fact]
    public async Task Operations_on_an_unknown_technician_report_not_found()
    {
        var unknown = Guid.NewGuid();

        Assert.IsType<CreateAbsenceOutcome.TechnicianNotFound>(await CreateAsync(unknown, _day, _day.AddDays(1)));

        await using var scope = database.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AbsenceService>();
        Assert.Null(await service.ListAsync(unknown, Cancellation));
        Assert.IsType<DeleteAbsenceOutcome.TechnicianNotFound>(await service.DeleteAsync(unknown, Guid.NewGuid(), Cancellation));
    }

    [Fact]
    public async Task Delete_removes_the_absence_and_is_idempotent()
    {
        var technicianId = await WorkforceTestData.CreateTechnicianAsync(database);
        var created = Assert.IsType<CreateAbsenceOutcome.Created>(await CreateAsync(technicianId, _day, _day.AddDays(1)));

        await using var scope = database.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AbsenceService>();
        Assert.IsType<DeleteAbsenceOutcome.Deleted>(await service.DeleteAsync(technicianId, created.Absence.Id, Cancellation));
        Assert.IsType<DeleteAbsenceOutcome.Deleted>(await service.DeleteAsync(technicianId, created.Absence.Id, Cancellation));

        Assert.Empty((await service.ListAsync(technicianId, Cancellation))!);
    }
}
