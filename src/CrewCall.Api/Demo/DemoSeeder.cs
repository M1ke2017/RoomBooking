using CrewCall.Persistence;
using CrewCall.Resources.Equipment;
using CrewCall.Resources.Vehicles;
using CrewCall.Scheduling.Assignments;
using CrewCall.Scheduling.Checks;
using CrewCall.Scheduling.Ports;
using CrewCall.WorkOrders.Customers;
using CrewCall.WorkOrders.Executions;
using CrewCall.WorkOrders.Incidents;
using CrewCall.WorkOrders.Sites;
using CrewCall.WorkOrders.Visits;
using CrewCall.WorkOrders;
using CrewCall.Workforce.Skills;
using CrewCall.Workforce.Technicians;
using CrewCall.Workforce.WorkingHours;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CrewCall.Api.Demo;

/// <param name="Day">The local (Warsaw) day the plan is for.</param>
/// <param name="CreatedDayPlan">False when the day already had its demo plan: nothing was written.</param>
/// <param name="IncidentId">The day's urgent incident.</param>
public sealed record DemoSeedResult(DateOnly Day, bool CreatedDayPlan, Guid IncidentId);

/// <summary>
/// Portfolio demo data (Sprint 16): a small company and a planned day in which an urgent incident collides with
/// lower-priority work, so the whole manager flow can be shown from /operations without creating anything by hand.
/// Everything goes through the modules' own services, exactly as the API does, so history, outbox, live updates and
/// reporting see it like real work. Idempotent: master data is found by its natural key (email, name, registration,
/// asset code, skill code) and created only when missing; the day plan is created at most once per local day.
/// </summary>
/// <remarks>
/// The plan is anchored on the current time (rounded up to the quarter hour, kept between 09:00 and 16:00 Warsaw time) so
/// the conflict is always ahead: completed work before it, work in progress around it, the incident just after it.
/// </remarks>
public sealed class DemoSeeder(
    CrewCallDbContext db,
    TimeProvider clock,
    CustomerService customers,
    SiteService sites,
    TechnicianService technicians,
    SkillService skills,
    WorkingHoursService workingHours,
    VehicleService vehicles,
    EquipmentService equipment,
    IVisitSchedulingSource visitSource,
    SchedulingCheckService checks,
    IActiveAssignmentCheck activeAssignments,
    IncidentService incidents)
{
    public const string TimeZoneId = "Europe/Warsaw";
    public const string Electrical = "ELECTRICAL";
    public const string Hvac = "HVAC";
    public const string Fiber = "FIBER";
    public const string IncidentTitle = "Awaria zasilania — sala zabiegowa";

    public static readonly (string Name, string Email, string Phone, string[] Skills)[] Team =
    [
        ("Jan Kowalski", "jan.kowalski@demo.crewcall.app", "+48 601 100 201", [Electrical, Hvac]),
        ("Anna Nowak", "anna.nowak@demo.crewcall.app", "+48 601 100 202", [Electrical, Fiber]),
        ("Piotr Wiśniewski", "piotr.wisniewski@demo.crewcall.app", "+48 601 100 203", [Hvac])
    ];

    private static readonly DateTimeZone Warsaw = DateTimeZoneProviders.Tzdb[TimeZoneId];

    public async Task<DemoSeedResult> SeedAsync(CancellationToken cancellationToken)
    {
        var skillIds = new Dictionary<string, Guid>
        {
            [Electrical] = await SkillAsync("Instalacje elektryczne", Electrical, cancellationToken),
            [Hvac] = await SkillAsync("Klimatyzacja i wentylacja", Hvac, cancellationToken),
            [Fiber] = await SkillAsync("Światłowody", Fiber, cancellationToken)
        };

        var team = new List<Guid>();
        foreach (var member in Team)
        {
            team.Add(await TechnicianAsync(member.Name, member.Email, member.Phone, member.Skills.Select(code => skillIds[code]), cancellationToken));
        }

        var (jan, anna, piotr) = (team[0], team[1], team[2]);
        var abc = await CustomerAsync("ABC Instalacje Sp. z o.o.", cancellationToken);
        var hospital = await CustomerAsync("Szpital Miejski w Gliwicach", cancellationToken);
        var tychy = await SiteAsync(abc, "Biuro Tychy", "ul. Przemysłowa 12", "Tychy", cancellationToken);
        var katowice = await SiteAsync(abc, "Magazyn Katowice", "ul. Logistyczna 4", "Katowice", cancellationToken);
        var gliwice = await SiteAsync(hospital, "Szpital — budynek B", "ul. Zdrowia 1", "Gliwice", cancellationToken);
        var transit = await VehicleAsync("ST 1234A", "Ford Transit", cancellationToken);
        var master = await VehicleAsync("SK 5678B", "Renault Master", cancellationToken);
        var meter = await EquipmentAsync("Miernik izolacji", "DEMO-MTR-01", cancellationToken);
        await EquipmentAsync("Agregat prądotwórczy", "DEMO-GEN-01", cancellationToken);
        await EquipmentAsync("Drabina 3 m", "DEMO-LAD-01", cancellationToken);

        // The day plan, once per local day.
        var now = clock.GetUtcNow();
        var localNow = Instant.FromDateTimeOffset(now).InZone(Warsaw);
        var day = DateOnly.FromDateTime(localNow.Date.ToDateTimeUnspecified());
        var dayStart = Warsaw.AtStartOfDay(localNow.Date).ToDateTimeOffset().ToUniversalTime();
        var dayEnd = Warsaw.AtStartOfDay(localNow.Date.PlusDays(1)).ToDateTimeOffset().ToUniversalTime();

        var existing = await db.Incidents.AsNoTracking()
            .Where(i => i.Title == IncidentTitle && i.SiteId == gliwice && i.RequestedStart >= dayStart && i.RequestedStart < dayEnd)
            .Select(i => (Guid?)i.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is { } incidentId)
        {
            return new DemoSeedResult(day, false, incidentId);
        }

        var anchor = Anchor(localNow);

        // The morning already happened: its plan and field work are replayed at their own times (never later than now),
        // so the history and the day's report show real travel, work and break times. Everything else is planned now.
        DateTimeOffset Morning(double minutes) => Min(anchor.AddMinutes(minutes), now);

        // Done this morning: Jan installed air conditioning in Tychy (25 min travel, 85 min on site with a 15 min break).
        var done = await VisitAsync(Morning(-240), abc, tychy, "Instalacja klimatyzacji", "Normal", anchor.AddHours(-3), anchor.AddHours(-1.5), cancellationToken);
        await AssignAsync(Morning(-240), done, jan, transit, [], 0, cancellationToken);
        await FieldWorkAsync(done, cancellationToken,
            ("start-travel", Morning(-205)), ("start-work", Morning(-180)), ("pause", Morning(-135)), ("resume", Morning(-120)), ("complete", Morning(-95)));

        // In progress now: Anna lays fibre in Tychy (an active work order).
        var fibre = await VisitAsync(Morning(-240), abc, tychy, "Montaż światłowodu", "High", anchor.AddHours(-1), anchor.AddHours(3), cancellationToken);
        await AssignAsync(Morning(-240), fibre, anna, master, [meter], 0, cancellationToken);
        await FieldWorkAsync(fibre, cancellationToken, ("start-travel", Morning(-80)), ("start-work", Morning(-60)));

        // Planned: Jan's routine inspection in Katowice — the visit the urgent incident will need to move.
        var inspection = await VisitAsync(now, abc, katowice, "Przegląd instalacji elektrycznej", "Normal", anchor.AddHours(1), anchor.AddHours(2.5), cancellationToken);
        await AssignAsync(now, inspection, jan, transit, [], 30, cancellationToken);

        // Planned: Piotr's air-conditioning service (he has no electrical skill).
        var service = await VisitAsync(now, abc, tychy, "Serwis klimatyzacji", "Normal", anchor.AddHours(0.5), anchor.AddHours(2), cancellationToken);
        await AssignAsync(now, service, piotr, null, [], 0, cancellationToken);

        // Waiting for a technician.
        await VisitAsync(now, abc, katowice, "Wymiana oświetlenia awaryjnego", "Low", anchor.AddHours(3), anchor.AddHours(4), cancellationToken);

        // The urgent incident: the hospital loses power while every electrician is busy.
        var incident = await incidents.CreateAsync(
            new CreateIncident(
                hospital, gliwice, IncidentTitle, "Brak zasilania w budynku B, agregat nie przejął obciążenia.", "Critical",
                anchor.AddHours(1.5), anchor.AddHours(3), [Electrical]),
            cancellationToken);
        var created = incident as CreateIncidentOutcome.Created ?? throw Unexpected("incident", incident);
        return new DemoSeedResult(day, true, created.Incident.Id);
    }

    /// <summary>Now rounded up to the quarter hour, kept between 09:00 and 16:00 local time (inside working hours).</summary>
    private static DateTimeOffset Anchor(ZonedDateTime localNow)
    {
        var minutes = (int)Math.Ceiling(localNow.TimeOfDay.ToTimeOnly().ToTimeSpan().TotalMinutes / 15) * 15;
        var clamped = Math.Clamp(minutes, 9 * 60, 16 * 60);
        return Warsaw.AtStrictly(localNow.Date.At(LocalTime.FromMinutesSinceMidnight(clamped))).ToDateTimeOffset().ToUniversalTime();
    }

    private async Task<Guid> SkillAsync(string name, string code, CancellationToken cancellationToken) =>
        await db.Skills.Where(s => s.Code == code).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(cancellationToken)
        ?? (await skills.CreateAsync(new CreateSkill(name, code, true), cancellationToken) is CreateSkillOutcome.Created created
            ? created.Skill.Id
            : throw new InvalidOperationException($"Demo skill {code} could not be created."));

    /// <summary>A technician working 06:00–22:00 every day, with the given skills.</summary>
    private async Task<Guid> TechnicianAsync(string name, string email, string phone, IEnumerable<Guid> skillIds, CancellationToken cancellationToken)
    {
        if (await db.Technicians.Where(t => t.Email == email).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(cancellationToken) is { } id)
        {
            return id;
        }

        var outcome = await technicians.CreateAsync(new CreateTechnician(name, email, true, TimeZoneId, "PL", phone), cancellationToken);
        var technician = (outcome as CreateTechnicianOutcome.Created ?? throw Unexpected("technician", outcome)).Technician.Id;
        foreach (var weekday in Enum.GetNames<DayOfWeek>())
        {
            await workingHours.CreateAsync(new CreateWorkingHours(technician, weekday, "06:00", "22:00"), cancellationToken);
        }

        foreach (var skillId in skillIds)
        {
            await skills.AssignToTechnicianAsync(technician, skillId, cancellationToken);
        }

        return technician;
    }

    private async Task<Guid> CustomerAsync(string name, CancellationToken cancellationToken) =>
        await db.Customers.Where(c => c.Name == name).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(cancellationToken)
        ?? (await customers.CreateAsync(new CreateCustomer(name, null), cancellationToken) is CreateCustomerOutcome.Created created
            ? created.Customer.Id
            : throw new InvalidOperationException($"Demo customer {name} could not be created."));

    private async Task<Guid> SiteAsync(Guid customerId, string name, string address, string city, CancellationToken cancellationToken) =>
        await db.Sites.Where(s => s.CustomerId == customerId && s.Name == name).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(cancellationToken)
        ?? (await sites.CreateAsync(new CreateSite(customerId, name, address, city, null, "PL", null, null), cancellationToken) is CreateSiteOutcome.Created created
            ? created.Site.Id
            : throw new InvalidOperationException($"Demo site {name} could not be created."));

    private async Task<Guid> VehicleAsync(string registration, string name, CancellationToken cancellationToken) =>
        await db.Vehicles.Where(v => v.RegistrationNumber == registration).Select(v => (Guid?)v.Id).FirstOrDefaultAsync(cancellationToken)
        ?? (await vehicles.CreateAsync(new CreateVehicle(registration, name, "Van", true), cancellationToken) is CreateVehicleOutcome.Created created
            ? created.Vehicle.Id
            : throw new InvalidOperationException($"Demo vehicle {registration} could not be created."));

    private async Task<Guid> EquipmentAsync(string name, string assetCode, CancellationToken cancellationToken) =>
        await db.Equipment.Where(e => e.AssetCode == assetCode).Select(e => (Guid?)e.Id).FirstOrDefaultAsync(cancellationToken)
        ?? (await equipment.CreateAsync(new CreateEquipment(name, assetCode, true), cancellationToken) is CreateEquipmentOutcome.Created created
            ? created.Equipment.Id
            : throw new InvalidOperationException($"Demo equipment {assetCode} could not be created."));

    /// <summary>A work order of the priority at the site, with one visit over [start, end), created at <paramref name="at"/>.</summary>
    private async Task<Guid> VisitAsync(
        DateTimeOffset at, Guid customerId, Guid siteId, string title, string priority, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var clock = new FixedClock(at);
        var workOrder = await new WorkOrderService(db, clock).CreateAsync(new CreateWorkOrder(customerId, siteId, title, null, priority), cancellationToken);
        var workOrderId = (workOrder as CreateWorkOrderOutcome.Created ?? throw Unexpected("work order", workOrder)).WorkOrder.Id;
        var visit = await new VisitService(db, clock).CreateAsync(new CreateVisit(workOrderId, start, end, null), cancellationToken);
        return (visit as CreateVisitOutcome.Created ?? throw Unexpected("visit", visit)).Visit.Id;
    }

    private async Task AssignAsync(
        DateTimeOffset at, Guid visitId, Guid technicianId, Guid? vehicleId, Guid[] equipmentIds, int travelBefore, CancellationToken cancellationToken)
    {
        var outcome = await new AssignmentService(db, visitSource, checks, new FixedClock(at)).CreateAsync(
            new CreateAssignment(visitId, technicianId, vehicleId, equipmentIds, null, travelBefore, 0), cancellationToken);
        _ = outcome as AssignOutcome.Assigned ?? throw Unexpected("assignment", outcome);
    }

    /// <summary>The technician's field work, each step at its own moment.</summary>
    private async Task FieldWorkAsync(Guid visitId, CancellationToken cancellationToken, params (string Step, DateTimeOffset At)[] steps)
    {
        foreach (var (step, at) in steps)
        {
            var executions = new VisitExecutionService(db, activeAssignments, new FixedClock(at));
            var outcome = step switch
            {
                "start-travel" => await executions.StartTravelAsync(visitId, cancellationToken),
                "start-work" => await executions.StartWorkAsync(visitId, cancellationToken),
                "pause" => await executions.PauseAsync(visitId, cancellationToken),
                "resume" => await executions.ResumeAsync(visitId, cancellationToken),
                _ => await executions.CompleteAsync(visitId, cancellationToken)
            };
            _ = outcome as VisitExecutionOutcome.Changed ?? throw Unexpected($"field work ({step})", outcome);
        }
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    /// <summary>The seed's own clock: a write at a chosen moment of the demo day.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static InvalidOperationException Unexpected(string what, object outcome) =>
        new($"The demo {what} could not be created: {outcome}.");
}
