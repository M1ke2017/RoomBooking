using CrewCall.Persistence;
using CrewCall.WorkOrders.Incidents;
using CrewCall.WorkOrders.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static CrewCall.WorkOrders.Tests.WorkOrdersTestData;

namespace CrewCall.WorkOrders.Tests;

public sealed class IncidentServiceTests(WorkOrdersDatabase database)
{
    private static readonly DateTimeOffset Start = new(2035, 3, 1, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static async Task<Incident> CreateIncidentAsync(AsyncServiceScope scope, string priority = "Urgent", params string[] skills)
    {
        var customerId = await CreateCustomerAsync(scope);
        var siteId = await CreateSiteAsync(scope, customerId);
        var outcome = await scope.ServiceProvider.GetRequiredService<IncidentService>().CreateAsync(
            new CreateIncident(customerId, siteId, "Power outage", null, priority, Start, Start.AddHours(2), skills), Cancellation);
        return Assert.IsType<CreateIncidentOutcome.Created>(outcome).Incident;
    }

    private static async Task<Incident> ReadyIncidentAsync(AsyncServiceScope scope, string priority = "Urgent")
    {
        var incidents = scope.ServiceProvider.GetRequiredService<IncidentService>();
        var incident = await CreateIncidentAsync(scope, priority);
        Assert.IsType<ChangeIncidentOutcome.Changed>(await incidents.BeginAnalysisAsync(incident.Id, Cancellation));
        Assert.IsType<ChangeIncidentOutcome.Changed>(await incidents.CompleteAnalysisAsync(
            new RecordIncidentAnalysis(incident.Id, Start, 1, 1, 0, 1, 0, 0), Cancellation));
        return incident;
    }

    private static StageIncidentDispatch Plan(Guid incidentId) =>
        new(incidentId, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.NewGuid(), null, []);

    [Theory]
    [InlineData(IncidentStatus.New, IncidentStatus.Analyzing)]
    [InlineData(IncidentStatus.New, IncidentStatus.Cancelled)]
    [InlineData(IncidentStatus.Analyzing, IncidentStatus.ReadyForDispatch)]
    [InlineData(IncidentStatus.Analyzing, IncidentStatus.Cancelled)]
    [InlineData(IncidentStatus.ReadyForDispatch, IncidentStatus.Dispatched)]
    [InlineData(IncidentStatus.ReadyForDispatch, IncidentStatus.Cancelled)]
    [InlineData(IncidentStatus.Dispatched, IncidentStatus.Resolved)]
    public void Lifecycle_allows_exactly_the_documented_transitions(IncidentStatus from, IncidentStatus to)
    {
        Assert.True(IncidentLifecycle.CanTransition(from, to));

        var allowed = Enum.GetValues<IncidentStatus>()
            .SelectMany(source => Enum.GetValues<IncidentStatus>().Select(target => (source, target)))
            .Count(pair => IncidentLifecycle.CanTransition(pair.source, pair.target));
        Assert.Equal(7, allowed);
    }

    [Theory]
    [InlineData(IncidentStatus.Resolved)]
    [InlineData(IncidentStatus.Cancelled)]
    public void Resolved_and_cancelled_are_terminal(IncidentStatus terminal)
    {
        Assert.True(IncidentLifecycle.IsTerminal(terminal));
        Assert.All(Enum.GetValues<IncidentStatus>(), target => Assert.False(IncidentLifecycle.CanTransition(terminal, target)));
    }

    [Theory]
    [InlineData(IncidentPriority.High, WorkOrderPriority.High)]
    [InlineData(IncidentPriority.Urgent, WorkOrderPriority.Urgent)]
    [InlineData(IncidentPriority.Critical, WorkOrderPriority.Urgent)]
    public void Incident_priority_maps_to_a_work_order_priority(IncidentPriority incident, WorkOrderPriority workOrder) =>
        Assert.Equal(workOrder, IncidentLifecycle.ToWorkOrderPriority(incident));

    [Fact]
    public async Task Create_saves_required_skills_as_rows_and_appends_IncidentCreated()
    {
        await using var scope = database.CreateScope();

        var incident = await CreateIncidentAsync(scope, "High", " fiber ", "Hvac");

        var db = scope.ServiceProvider.GetRequiredService<CrewCallDbContext>();
        var rows = await db.IncidentRequiredSkills.AsNoTracking().Where(s => s.IncidentId == incident.Id).Select(s => s.SkillCode).ToListAsync(Cancellation);
        Assert.Equal(["FIBER", "HVAC"], rows.Order());
        Assert.Equal((IncidentStatus.New, IncidentPriority.High), (incident.Status, incident.Priority));
        var created = await db.OperationalEvents.AsNoTracking().SingleAsync(e => e.AggregateId == incident.Id, Cancellation);
        Assert.Equal((IncidentEvents.IncidentCreated, IncidentEvents.IncidentAggregate), (created.EventType, created.AggregateType));
    }

    [Fact]
    public async Task Create_rejects_duplicate_skill_codes_after_normalization()
    {
        await using var scope = database.CreateScope();
        var customerId = await CreateCustomerAsync(scope);
        var siteId = await CreateSiteAsync(scope, customerId);

        var outcome = await scope.ServiceProvider.GetRequiredService<IncidentService>().CreateAsync(
            new CreateIncident(customerId, siteId, "Leak", null, "High", Start, Start.AddHours(1), ["gas", " GAS"]), Cancellation);

        Assert.Contains("requiredSkillCodes", Assert.IsType<CreateIncidentOutcome.Invalid>(outcome).Errors.Keys);
    }

    [Fact]
    public async Task Status_changes_follow_the_lifecycle_and_record_their_events()
    {
        await using var scope = database.CreateScope();
        var incidents = scope.ServiceProvider.GetRequiredService<IncidentService>();
        var db = scope.ServiceProvider.GetRequiredService<CrewCallDbContext>();
        var incident = await ReadyIncidentAsync(scope);

        Assert.IsType<ChangeIncidentOutcome.TransitionNotAllowed>(await incidents.ResolveAsync(incident.Id, Cancellation));

        var staged = Assert.IsType<StageIncidentDispatchOutcome.Staged>(await incidents.StageDispatchAsync(Plan(incident.Id), Cancellation));
        await db.SaveChangesAsync(Cancellation);
        Assert.IsType<ChangeIncidentOutcome.TransitionNotAllowed>(await incidents.CancelAsync(incident.Id, Cancellation));
        Assert.IsType<ChangeIncidentOutcome.TransitionNotAllowed>(await incidents.BeginAnalysisAsync(incident.Id, Cancellation));

        var resolved = Assert.IsType<ChangeIncidentOutcome.Changed>(await incidents.ResolveAsync(incident.Id, Cancellation)).Incident;
        Assert.Equal((IncidentStatus.Resolved, (Guid?)staged.WorkOrder.Id), (resolved.Status, resolved.WorkOrderId));
        Assert.NotNull(resolved.ResolvedAtUtc);

        var events = await db.OperationalEvents.AsNoTracking()
            .Where(e => e.AggregateId == incident.Id).OrderBy(e => e.Sequence).Select(e => e.EventType).ToListAsync(Cancellation);
        Assert.Equal(
            [IncidentEvents.IncidentCreated, IncidentEvents.IncidentAnalyzed, IncidentEvents.IncidentDispatched, IncidentEvents.IncidentResolved],
            events);
    }

    [Fact]
    public async Task Staging_a_dispatch_writes_nothing_until_the_caller_saves()
    {
        await using var scope = database.CreateScope();
        var incidents = scope.ServiceProvider.GetRequiredService<IncidentService>();
        var incident = await ReadyIncidentAsync(scope, "Critical");
        var plan = Plan(incident.Id);

        var staged = Assert.IsType<StageIncidentDispatchOutcome.Staged>(await incidents.StageDispatchAsync(plan, Cancellation));

        Assert.Equal((WorkOrderPriority.Urgent, incident.Title, incident.SiteId), (staged.WorkOrder.Priority, staged.WorkOrder.Title, staged.WorkOrder.SiteId));
        Assert.Equal((plan.VisitId, incident.RequestedStart, incident.RequestedEnd), (staged.Visit.Id, staged.Visit.Start, staged.Visit.End));
        await using (var other = database.CreateScope())
        {
            var reader = other.ServiceProvider.GetRequiredService<CrewCallDbContext>();
            Assert.False(await reader.WorkOrders.AnyAsync(w => w.Id == staged.WorkOrder.Id, Cancellation));
            Assert.Equal(IncidentStatus.ReadyForDispatch, (await reader.Incidents.AsNoTracking().SingleAsync(i => i.Id == incident.Id, Cancellation)).Status);
        }
    }

    [Fact]
    public async Task Staging_refuses_an_incident_not_ready_or_already_dispatched()
    {
        await using var scope = database.CreateScope();
        var incidents = scope.ServiceProvider.GetRequiredService<IncidentService>();
        var fresh = await CreateIncidentAsync(scope);
        var ready = await ReadyIncidentAsync(scope);
        Assert.IsType<StageIncidentDispatchOutcome.Staged>(await incidents.StageDispatchAsync(Plan(ready.Id), Cancellation));
        await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().SaveChangesAsync(Cancellation);

        Assert.IsType<StageIncidentDispatchOutcome.NotReady>(await incidents.StageDispatchAsync(Plan(fresh.Id), Cancellation));
        Assert.IsType<StageIncidentDispatchOutcome.AlreadyDispatched>(await incidents.StageDispatchAsync(Plan(ready.Id), Cancellation));
        Assert.IsType<StageIncidentDispatchOutcome.NotFound>(await incidents.StageDispatchAsync(Plan(Guid.NewGuid()), Cancellation));
    }

    [Fact]
    public async Task The_row_version_lets_only_one_of_two_concurrent_dispatches_commit()
    {
        Guid incidentId;
        await using (var setup = database.CreateScope())
        {
            incidentId = (await ReadyIncidentAsync(setup)).Id;
        }

        await using var first = database.CreateScope();
        await using var second = database.CreateScope();
        Assert.IsType<StageIncidentDispatchOutcome.Staged>(
            await first.ServiceProvider.GetRequiredService<IncidentService>().StageDispatchAsync(Plan(incidentId), Cancellation));
        Assert.IsType<StageIncidentDispatchOutcome.Staged>(
            await second.ServiceProvider.GetRequiredService<IncidentService>().StageDispatchAsync(Plan(incidentId), Cancellation));

        await first.ServiceProvider.GetRequiredService<CrewCallDbContext>().SaveChangesAsync(Cancellation);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => second.ServiceProvider.GetRequiredService<CrewCallDbContext>().SaveChangesAsync(Cancellation));
    }

    [Theory]
    [InlineData("duplicate skill", "23505")]
    [InlineData("lower-case skill", "23514")]
    [InlineData("blank skill", "23514")]
    [InlineData("site of another customer", "23503")]
    [InlineData("empty window", "23514")]
    [InlineData("dispatched without a work order", "23514")]
    [InlineData("resolved without a resolution time", "23514")]
    [InlineData("unknown priority", "23514")]
    public async Task The_database_rejects_inconsistent_incidents(string violation, string sqlState)
    {
        await using var scope = database.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrewCallDbContext>();
        var incident = await CreateIncidentAsync(scope, "High", "FIBER");
        var otherSite = await CreateSiteAsync(scope, await CreateCustomerAsync(scope));

        var sql = violation switch
        {
            "duplicate skill" => $"INSERT INTO workorders.incident_required_skills VALUES ('{incident.Id}', 'FIBER')",
            "lower-case skill" => $"INSERT INTO workorders.incident_required_skills VALUES ('{incident.Id}', 'hvac')",
            "blank skill" => $"INSERT INTO workorders.incident_required_skills VALUES ('{incident.Id}', ' ')",
            "site of another customer" => $"UPDATE workorders.incidents SET site_id = '{otherSite}' WHERE id = '{incident.Id}'",
            "empty window" => $"UPDATE workorders.incidents SET requested_end = requested_start WHERE id = '{incident.Id}'",
            "dispatched without a work order" => $"UPDATE workorders.incidents SET status = 'Dispatched' WHERE id = '{incident.Id}'",
            "resolved without a resolution time" => $"UPDATE workorders.incidents SET status = 'Resolved' WHERE id = '{incident.Id}'",
            _ => $"UPDATE workorders.incidents SET priority = 'Low' WHERE id = '{incident.Id}'"
        };

#pragma warning disable EF1002 // test-only SQL built from Guids
        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, Cancellation));
#pragma warning restore EF1002
        Assert.Equal(sqlState, exception.SqlState);
    }
}
