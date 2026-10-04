using CrewCall.Persistence;
using CrewCall.Persistence.Operations;
using CrewCall.WorkOrders.Operations;
using CrewCall.WorkOrders.Visits;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static CrewCall.WorkOrders.Tests.WorkOrdersTestData;

namespace CrewCall.WorkOrders.Tests;

public sealed class OperationalEventTests(WorkOrdersDatabase database)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_failed_event_insert_rolls_back_the_status_change()
    {
        Guid workOrderId;
        await using (var setup = database.CreateScope())
        {
            workOrderId = (await CreateWorkOrderAsync(setup)).Id;
        }

        await using (await RejectEventsForAsync(workOrderId))
        {
            await using var scope = database.CreateScope();
            var workOrders = scope.ServiceProvider.GetRequiredService<WorkOrderService>();

            await Assert.ThrowsAsync<DbUpdateException>(() =>
                workOrders.ChangeStatusAsync(new ChangeWorkOrderStatus(workOrderId, "Planned"), Cancellation));
        }

        // Read through a fresh scope: what is in the database, not in a change tracker.
        await using var read = database.CreateScope();
        var saved = await read.ServiceProvider.GetRequiredService<WorkOrderService>().GetAsync(workOrderId, Cancellation);
        Assert.Equal(WorkOrderStatus.Open, saved!.Status);
        var events = await read.ServiceProvider.GetRequiredService<OperationalEventLog>()
            .ListForAggregateAsync(WorkOrderEvents.WorkOrderAggregate, workOrderId, Cancellation);
        Assert.Equal(WorkOrderEvents.WorkOrderCreated, Assert.Single(events).EventType);
    }

    [Fact]
    public async Task A_failed_event_insert_rolls_back_the_visit_creation()
    {
        Guid workOrderId;
        await using (var setup = database.CreateScope())
        {
            workOrderId = (await CreateWorkOrderAsync(setup)).Id;
        }

        // The VisitCreated event is rejected via the visit's work order id carried in its payload.
        await using (await RejectEventsWherePayloadReferencesAsync(workOrderId))
        {
            await using var scope = database.CreateScope();
            var visits = scope.ServiceProvider.GetRequiredService<VisitService>();
            var start = new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);

            await Assert.ThrowsAsync<DbUpdateException>(() =>
                visits.CreateAsync(new CreateVisit(workOrderId, start, start.AddHours(1), null), Cancellation));
        }

        await using var read = database.CreateScope();
        Assert.Empty((await read.ServiceProvider.GetRequiredService<VisitService>().ListForWorkOrderAsync(workOrderId, Cancellation))!);
    }

    [Fact]
    public async Task Operational_events_cannot_be_updated_or_deleted()
    {
        Guid workOrderId;
        await using (var setup = database.CreateScope())
        {
            workOrderId = (await CreateWorkOrderAsync(setup)).Id;
        }

        await using var scope = database.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrewCallDbContext>();

        var update = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"UPDATE ops.operational_events SET event_type = 'Tampered' WHERE aggregate_id = {workOrderId}", Cancellation));
        var delete = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"DELETE FROM ops.operational_events WHERE aggregate_id = {workOrderId}", Cancellation));

        Assert.Equal(PostgresErrorCodes.RestrictViolation, update.SqlState);
        Assert.Equal(PostgresErrorCodes.RestrictViolation, delete.SqlState);
        var events = await scope.ServiceProvider.GetRequiredService<OperationalEventLog>()
            .ListForAggregateAsync(WorkOrderEvents.WorkOrderAggregate, workOrderId, Cancellation);
        Assert.Equal(WorkOrderEvents.WorkOrderCreated, Assert.Single(events).EventType);
    }

    /// <summary>Installs a trigger that rejects event inserts for one aggregate only, so parallel tests are unaffected.</summary>
    private Task<TemporaryTrigger> RejectEventsForAsync(Guid aggregateId) =>
        TemporaryTrigger.InstallAsync(database, $"NEW.aggregate_id = '{aggregateId}'");

    private Task<TemporaryTrigger> RejectEventsWherePayloadReferencesAsync(Guid workOrderId) =>
        TemporaryTrigger.InstallAsync(database, $"NEW.payload ->> 'workOrderId' = '{workOrderId}' AND NEW.event_type = 'VisitCreated'");

    private sealed class TemporaryTrigger(WorkOrdersDatabase database, string name) : IAsyncDisposable
    {
        public static async Task<TemporaryTrigger> InstallAsync(WorkOrdersDatabase database, string condition)
        {
            var name = $"test_reject_{Guid.NewGuid():N}";
            await ExecuteAsync(database, $"""
                CREATE FUNCTION ops.{name}() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'test: event insert rejected'; END; $$;
                CREATE TRIGGER {name} BEFORE INSERT ON ops.operational_events
                    FOR EACH ROW WHEN ({condition}) EXECUTE FUNCTION ops.{name}();
                """);
            return new TemporaryTrigger(database, name);
        }

        public async ValueTask DisposeAsync() =>
            await ExecuteAsync(database, $"DROP TRIGGER {name} ON ops.operational_events; DROP FUNCTION ops.{name}();");

        private static async Task ExecuteAsync(WorkOrdersDatabase database, string sql)
        {
            await using var scope = database.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().Database.ExecuteSqlRawAsync(sql, Cancellation);
        }
    }
}
