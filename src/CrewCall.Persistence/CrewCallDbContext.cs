using System.Text.Json;
using System.Text.Json.Serialization;
using CrewCall.Persistence.Operations;
using CrewCall.Resources;
using CrewCall.Resources.Equipment;
using CrewCall.Resources.Vehicles;
using CrewCall.WorkOrders;
using CrewCall.WorkOrders.Customers;
using CrewCall.WorkOrders.Sites;
using CrewCall.WorkOrders.Visits;
using CrewCall.Workforce;
using CrewCall.Workforce.Absences;
using CrewCall.Workforce.Holidays;
using CrewCall.Workforce.Skills;
using CrewCall.Workforce.Teams;
using CrewCall.Workforce.Technicians;
using CrewCall.Workforce.WorkingHours;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.Persistence;

/// <summary>
/// The CrewCall database context: one physical database, one schema per module (<see cref="DatabaseSchemas"/>).
/// Each module works through its own narrow interface and sees only the sets it owns.
/// Entity mapping lives in <c>Configurations/</c>, one <see cref="IEntityTypeConfiguration{TEntity}"/> per entity.
/// </summary>
public sealed class CrewCallDbContext(DbContextOptions<CrewCallDbContext> options)
    : DbContext(options), IWorkOrdersDbContext, IWorkforceDbContext, IResourcesDbContext
{
    private static readonly JsonSerializerOptions _payloadJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Site> Sites => Set<Site>();

    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();

    public DbSet<Visit> Visits => Set<Visit>();

    public DbSet<OperationalEvent> OperationalEvents => Set<OperationalEvent>();

    public DbSet<Technician> Technicians => Set<Technician>();

    public DbSet<Skill> Skills => Set<Skill>();

    public DbSet<TechnicianSkill> TechnicianSkills => Set<TechnicianSkill>();

    public DbSet<Team> Teams => Set<Team>();

    public DbSet<TechnicianWorkingHours> TechnicianWorkingHours => Set<TechnicianWorkingHours>();

    public DbSet<TechnicianAbsence> TechnicianAbsences => Set<TechnicianAbsence>();

    public DbSet<HolidayCalendarEntry> HolidayCalendar => Set<HolidayCalendarEntry>();

    public DbSet<Vehicle> Vehicles => Set<Vehicle>();

    public DbSet<EquipmentItem> Equipment => Set<EquipmentItem>();

    /// <inheritdoc cref="IWorkOrdersDbContext.AppendOperationalEvent"/>
    /// <remarks>
    /// Only adds the row to the change tracker: it is inserted by the caller's next SaveChanges, which EF Core runs in one
    /// transaction together with the state changes. There is no separate save for events.
    /// </remarks>
    public void AppendOperationalEvent(string eventType, string aggregateType, Guid aggregateId, DateTimeOffset occurredAtUtc, object payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateType);
        ArgumentNullException.ThrowIfNull(payload);

        var payloadJson = JsonSerializer.Serialize(payload, payload.GetType(), _payloadJsonOptions);
        OperationalEvents.Add(new OperationalEvent(
            Guid.CreateVersion7(), occurredAtUtc, eventType, aggregateType, aggregateId, payloadJson, correlationId: null));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // btree_gist: lets exclusion constraints combine "=" on uuid/text with "&&" on ranges (no-overlap rules).
        modelBuilder.HasPostgresExtension("btree_gist");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CrewCallDbContext).Assembly);
    }
}
