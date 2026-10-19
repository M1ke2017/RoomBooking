using System.Text.Json;
using System.Text.Json.Serialization;
using CrewCall.Contracts.Integration;
using CrewCall.Persistence.Messaging;
using CrewCall.Persistence.Operations;
using CrewCall.Resources;
using CrewCall.Resources.Equipment;
using CrewCall.Resources.Vehicles;
using CrewCall.Scheduling;
using CrewCall.Scheduling.Assignments;
using CrewCall.Scheduling.Reservations;
using CrewCall.WorkOrders;
using CrewCall.WorkOrders.Customers;
using CrewCall.WorkOrders.Executions;
using CrewCall.WorkOrders.Incidents;
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
/// <param name="correlation">The current request's correlation id, written to events and outbox messages.</param>
public sealed class CrewCallDbContext(
    DbContextOptions<CrewCallDbContext> options,
    CorrelationContext? correlation = null)
    : DbContext(options), IWorkOrdersDbContext, IWorkforceDbContext, IResourcesDbContext, ISchedulingDbContext, IOutboxWriter
{
    private static readonly JsonSerializerOptions _payloadJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Site> Sites => Set<Site>();

    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();

    public DbSet<Visit> Visits => Set<Visit>();

    public DbSet<Incident> Incidents => Set<Incident>();

    public DbSet<IncidentRequiredSkill> IncidentRequiredSkills => Set<IncidentRequiredSkill>();

    public DbSet<VisitExecution> VisitExecutions => Set<VisitExecution>();

    public DbSet<VisitExecutionPause> VisitExecutionPauses => Set<VisitExecutionPause>();

    public DbSet<OperationalEvent> OperationalEvents => Set<OperationalEvent>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    public DbSet<IntegrationEventReceipt> IntegrationEventReceipts => Set<IntegrationEventReceipt>();

    public DbSet<Technician> Technicians => Set<Technician>();

    public DbSet<Skill> Skills => Set<Skill>();

    public DbSet<TechnicianSkill> TechnicianSkills => Set<TechnicianSkill>();

    public DbSet<Team> Teams => Set<Team>();

    public DbSet<TechnicianWorkingHours> TechnicianWorkingHours => Set<TechnicianWorkingHours>();

    public DbSet<TechnicianAbsence> TechnicianAbsences => Set<TechnicianAbsence>();

    public DbSet<HolidayCalendarEntry> HolidayCalendar => Set<HolidayCalendarEntry>();

    public DbSet<Vehicle> Vehicles => Set<Vehicle>();

    public DbSet<EquipmentItem> Equipment => Set<EquipmentItem>();

    public DbSet<ResourceReservation> ResourceReservations => Set<ResourceReservation>();

    public DbSet<Assignment> Assignments => Set<Assignment>();

    public DbSet<AssignmentEquipment> AssignmentEquipment => Set<AssignmentEquipment>();

    /// <inheritdoc cref="IWorkOrdersDbContext.AppendOperationalEvent"/>
    /// <remarks>
    /// Only adds rows to the change tracker: they are inserted by the caller's next SaveChanges, which EF Core runs in one
    /// transaction together with the state changes. There is no separate save for events. When the event is one that is
    /// published (<see cref="IntegrationEventMapper"/>), its outbox message is added to the same unit of work, so the
    /// business change, its history and its outgoing message commit together or not at all (ADR-0014).
    /// </remarks>
    public void AppendOperationalEvent(string eventType, string aggregateType, Guid aggregateId, DateTimeOffset occurredAtUtc, object payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateType);
        ArgumentNullException.ThrowIfNull(payload);

        var correlationId = correlation?.CorrelationId;
        var payloadJson = JsonSerializer.Serialize(payload, payload.GetType(), _payloadJsonOptions);
        OperationalEvents.Add(new OperationalEvent(
            Guid.CreateVersion7(), occurredAtUtc, eventType, aggregateType, aggregateId, payloadJson, correlationId));

        if (IntegrationEventMapper.Map(eventType, occurredAtUtc, payload, correlationId) is { } integrationEvent)
        {
            AddOutboxMessage(integrationEvent, aggregateType, aggregateId);
        }
    }

    /// <inheritdoc cref="IOutboxWriter.Add"/>
    void IOutboxWriter.Add(IIntegrationEvent integrationEvent, string? aggregateType, Guid? aggregateId) =>
        AddOutboxMessage(integrationEvent, aggregateType, aggregateId);

    private void AddOutboxMessage(IIntegrationEvent integrationEvent, string? aggregateType, Guid? aggregateId)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var descriptor = IntegrationEventCatalog.Describe(integrationEvent);
        OutboxMessages.Add(new OutboxMessage(
            integrationEvent.EventId,
            integrationEvent.OccurredAtUtc,
            descriptor.Type,
            descriptor.Version,
            descriptor.RoutingKey,
            IntegrationEventCatalog.SerializePayload(integrationEvent),
            integrationEvent.CorrelationId,
            aggregateType,
            aggregateId,
            integrationEvent.OccurredAtUtc));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // btree_gist: lets exclusion constraints combine "=" on uuid/text with "&&" on ranges (no-overlap rules).
        modelBuilder.HasPostgresExtension("btree_gist");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CrewCallDbContext).Assembly);
    }
}
