using System.Net;
using System.Text.RegularExpressions;
using Daylo.Core;
using Daylo.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daylo.Tests;

/// <summary>
/// Daylo v0.1 foundation: right after startup the database is migrated, the demo day is there (with its conflict),
/// /health and /today work, and the database refuses two concurrent decisions about the same task.
/// </summary>
public sealed partial class FoundationTests(DayloApp app)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_demo_day_is_ready_after_startup_and_seeding_again_changes_nothing()
    {
        app.CreateClient(); // starts Daylo: migrations and the demo seed run on startup
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DayloDbContext>();

        var before = await CountsAsync(db);
        Assert.Equal((3, 3, 2, 3, 4, 5, 1, 4), before);

        // The conflict the urgent incident runs into: both electricians are busy at 10:30–12:00.
        var incident = await db.Incidents.Include(i => i.RequiredSkill).SingleAsync(i => i.Title == DemoData.IncidentTitle, Cancellation);
        Assert.Equal((Priority.Urgent, IncidentStatus.New, "ELECTRICAL"), (incident.Priority, incident.Status, incident.RequiredSkill!.Code));
        var busyElectricians = await db.Assignments
            .Where(a => a.Worker.Skills.Any(s => s.Code == "ELECTRICAL"))
            .Where(a => a.Task.PlannedStart < incident.RequestedEnd && a.Task.PlannedEnd > incident.RequestedStart)
            .Select(a => new { a.Worker.Name, a.Task.Title, a.Task.Priority })
            .OrderBy(a => a.Name)
            .ToListAsync(Cancellation);
        Assert.Equal(
            [("Anna Nowak", "Montaż światłowodu", Priority.High), ("Jan Kowalski", "Przegląd instalacji elektrycznej", Priority.Normal)],
            busyElectricians.Select(a => (a.Name, a.Title, a.Priority)));

        Assert.False(await DemoData.SeedAsync(db, DayloApp.Now, Cancellation));
        Assert.Equal(before, await CountsAsync(db));
    }

    [Fact]
    public async Task Health_is_healthy_when_the_database_answers()
    {
        using var response = await app.CreateClient().GetAsync("/health", Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(Cancellation));
    }

    [Fact]
    public async Task Today_shows_the_plan_the_team_and_the_urgent_incident_in_plain_words()
    {
        using var response = await app.CreateClient().GetAsync("/", Cancellation); // the start page is /today

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/today", response.RequestMessage!.RequestUri!.AbsolutePath);
        var page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Cancellation));

        Assert.Contains("Awaria zasilania — sala zabiegowa", page);
        Assert.Contains("Termin 10:30–12:00", page);
        Assert.Contains("10:00–11:30", page);
        Assert.Contains("Przegląd instalacji elektrycznej", page);
        Assert.Contains("Pracuje: Montaż światłowodu", page);
        Assert.Contains("Następne: 10:00 Przegląd instalacji elektrycznej", page);
        Assert.Contains("href=\"tel:+48601100201\"", page);
        Assert.Contains("Bez pracownika", page);
        Assert.DoesNotMatch(Guid(), page);
    }

    [Fact]
    public async Task Two_decisions_about_the_same_task_cannot_both_win()
    {
        app.CreateClient();
        Guid taskId;
        await using (var setup = app.Services.CreateAsyncScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<DayloDbContext>();
            var site = await db.Sites.FirstAsync(Cancellation);
            var task = new WorkTask
            {
                CustomerId = site.CustomerId, SiteId = site.Id, Title = "Zadanie testowe",
                PlannedStart = WorkDay.At(DayloApp.Day.AddDays(1), 10), PlannedEnd = WorkDay.At(DayloApp.Day.AddDays(1), 11)
            };
            db.Tasks.Add(task);
            await db.SaveChangesAsync(Cancellation);
            taskId = task.Id;
        }

        await using var first = app.Services.CreateAsyncScope();
        await using var second = app.Services.CreateAsyncScope();
        var firstDb = first.ServiceProvider.GetRequiredService<DayloDbContext>();
        var secondDb = second.ServiceProvider.GetRequiredService<DayloDbContext>();
        var seenByFirst = await firstDb.Tasks.SingleAsync(t => t.Id == taskId, Cancellation);
        var seenBySecond = await secondDb.Tasks.SingleAsync(t => t.Id == taskId, Cancellation);

        seenByFirst.PlannedStart = seenByFirst.PlannedStart.AddHours(2);
        seenBySecond.PlannedStart = seenBySecond.PlannedStart.AddHours(3);
        await firstDb.SaveChangesAsync(Cancellation);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync(Cancellation));
    }

    /// <summary>Skills, workers, customers, sites, resources, tasks of the demo day, incidents, active assignments.</summary>
    private static async Task<(int, int, int, int, int, int, int, int)> CountsAsync(DayloDbContext db)
    {
        var (start, end) = WorkDay.Range(DayloApp.Day);
        return (
            await db.Skills.CountAsync(Cancellation),
            await db.Workers.CountAsync(Cancellation),
            await db.Customers.CountAsync(Cancellation),
            await db.Sites.CountAsync(Cancellation),
            await db.Resources.CountAsync(Cancellation),
            await db.Tasks.CountAsync(t => t.PlannedStart >= start && t.PlannedStart < end, Cancellation),
            await db.Incidents.CountAsync(Cancellation),
            await db.Assignments.CountAsync(a => a.Status == AssignmentStatus.Active, Cancellation));
    }

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex Guid();
}
