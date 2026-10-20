using Daylo.Core;
using Microsoft.EntityFrameworkCore;

namespace Daylo.Web.Data;

/// <summary>
/// A small company and a planned day, so Daylo shows something right after it starts. Idempotent: people, customers,
/// sites and resources are found by name and created only when missing; the day plan is created once per local day.
/// </summary>
/// <remarks>
/// The day has one conflict built in: the hospital's power failure (10:30–12:00) needs an electrician, and both
/// electricians are busy — Anna on a high-priority job, Jan on a routine inspection that could be moved.
/// </remarks>
public static class DemoData
{
    public const string IncidentTitle = "Awaria zasilania — sala zabiegowa";

    /// <returns>True when the day plan was created; false when today already had it.</returns>
    public static async Task<bool> SeedAsync(DayloDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var electrical = await SkillAsync(db, "ELECTRICAL", "Instalacje elektryczne", cancellationToken);
        var hvac = await SkillAsync(db, "HVAC", "Klimatyzacja i wentylacja", cancellationToken);
        var fiber = await SkillAsync(db, "FIBER", "Światłowody", cancellationToken);

        var jan = await WorkerAsync(db, "Jan Kowalski", "+48 601 100 201", [electrical, hvac], cancellationToken);
        var anna = await WorkerAsync(db, "Anna Nowak", "+48 601 100 202", [electrical, fiber], cancellationToken);
        var piotr = await WorkerAsync(db, "Piotr Wiśniewski", "+48 601 100 203", [hvac], cancellationToken);

        var abc = await CustomerAsync(db, "ABC Instalacje Sp. z o.o.", cancellationToken);
        var hospital = await CustomerAsync(db, "Szpital Miejski w Gliwicach", cancellationToken);
        var tychy = await SiteAsync(db, abc, "Biuro Tychy", "ul. Przemysłowa 12, Tychy", cancellationToken);
        var katowice = await SiteAsync(db, abc, "Magazyn Katowice", "ul. Logistyczna 4, Katowice", cancellationToken);
        var gliwice = await SiteAsync(db, hospital, "Szpital — budynek B", "ul. Zdrowia 1, Gliwice", cancellationToken);

        await ResourceAsync(db, "Ford Transit ST 1234A", ResourceType.Vehicle, true, cancellationToken);
        await ResourceAsync(db, "Renault Master SK 5678B", ResourceType.Vehicle, true, cancellationToken);
        await ResourceAsync(db, "Miernik izolacji", ResourceType.Equipment, true, cancellationToken);
        await ResourceAsync(db, "Agregat prądotwórczy", ResourceType.Equipment, false, cancellationToken); // in service

        await db.SaveChangesAsync(cancellationToken);

        var day = WorkDay.Today(now);
        var (dayStart, dayEnd) = WorkDay.Range(day);
        if (await db.Incidents.AnyAsync(i => i.Title == IncidentTitle && i.RequestedStart >= dayStart && i.RequestedStart < dayEnd, cancellationToken))
        {
            return false;
        }

        Plan(db, day, abc, tychy, "Instalacja klimatyzacji", Priority.Normal, WorkTaskStatus.Completed, (7, 30), (9, 0), jan);
        Plan(db, day, abc, tychy, "Montaż światłowodu", Priority.High, WorkTaskStatus.InProgress, (8, 0), (13, 0), anna);
        Plan(db, day, abc, tychy, "Serwis klimatyzacji", Priority.Normal, WorkTaskStatus.Planned, (9, 30), (11, 0), piotr);
        Plan(db, day, abc, katowice, "Przegląd instalacji elektrycznej", Priority.Normal, WorkTaskStatus.Planned, (10, 0), (11, 30), jan);
        Plan(db, day, abc, katowice, "Wymiana oświetlenia awaryjnego", Priority.Low, WorkTaskStatus.Planned, (14, 0), (15, 0), null);

        var incident = new Incident
        {
            Customer = hospital,
            Site = gliwice,
            Title = IncidentTitle,
            Priority = Priority.Urgent,
            RequiredSkill = electrical,
            RequestedStart = WorkDay.At(day, 10, 30),
            RequestedEnd = WorkDay.At(day, 12, 0)
        };
        db.Incidents.Add(incident);
        db.ActivityLog.Add(new ActivityLog
        {
            Type = "DayPlanned",
            EntityId = incident.Id,
            Message = $"Plan dnia {day:dd.MM.yyyy} przygotowany: 5 zadań i 1 pilne zgłoszenie.",
            OccurredAt = now.ToUniversalTime()
        });

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static void Plan(
        DayloDbContext db, DateOnly day, Customer customer, Site site, string title, Priority priority, WorkTaskStatus status,
        (int Hour, int Minute) start, (int Hour, int Minute) end, Worker? worker)
    {
        var task = new WorkTask
        {
            Customer = customer,
            Site = site,
            Title = title,
            Priority = priority,
            Status = status,
            PlannedStart = WorkDay.At(day, start.Hour, start.Minute),
            PlannedEnd = WorkDay.At(day, end.Hour, end.Minute)
        };
        if (worker is not null)
        {
            task.Assignments.Add(new Assignment { Worker = worker });
        }

        db.Tasks.Add(task);
    }

    private static async Task<Skill> SkillAsync(DayloDbContext db, string code, string name, CancellationToken cancellationToken) =>
        await db.Skills.SingleOrDefaultAsync(s => s.Code == code, cancellationToken)
        ?? db.Skills.Add(new Skill { Code = code, Name = name }).Entity;

    private static async Task<Worker> WorkerAsync(DayloDbContext db, string name, string phone, Skill[] skills, CancellationToken cancellationToken) =>
        await db.Workers.SingleOrDefaultAsync(w => w.Name == name, cancellationToken)
        ?? db.Workers.Add(new Worker { Name = name, PhoneNumber = phone, Skills = [.. skills] }).Entity;

    private static async Task<Customer> CustomerAsync(DayloDbContext db, string name, CancellationToken cancellationToken) =>
        await db.Customers.SingleOrDefaultAsync(c => c.Name == name, cancellationToken)
        ?? db.Customers.Add(new Customer { Name = name }).Entity;

    private static async Task<Site> SiteAsync(DayloDbContext db, Customer customer, string name, string address, CancellationToken cancellationToken) =>
        await db.Sites.SingleOrDefaultAsync(s => s.CustomerId == customer.Id && s.Name == name, cancellationToken)
        ?? db.Sites.Add(new Site { Customer = customer, Name = name, Address = address }).Entity;

    private static async Task ResourceAsync(DayloDbContext db, string name, ResourceType type, bool isAvailable, CancellationToken cancellationToken)
    {
        if (!await db.Resources.AnyAsync(r => r.Name == name, cancellationToken))
        {
            db.Resources.Add(new Resource { Name = name, Type = type, IsAvailable = isAvailable });
        }
    }
}
