using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CrewCall.Contracts.Incidents;
using CrewCall.Persistence;
using CrewCall.Scheduling.Ports;
using CrewCall.Scheduling.Reservations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CrewCall.Api.Tests.IncidentTestKit;

namespace CrewCall.Api.Tests;

/// <summary>
/// Dispatch atomicity and concurrency on the real database. The races are forced, not hoped for: a gate in front of the
/// WorkOrders staging holds a dispatch after its final check has passed, inside its open transaction, so the database
/// guards (the reservations' exclusion constraint, the incident's row version) must decide.
/// </summary>
public sealed class IncidentDispatchRaceTests(CrewCallApiFactory factory)
{
    [Fact]
    public async Task A_claim_lost_after_the_final_check_rolls_back_the_whole_dispatch()
    {
        var gate = new DispatchStagingGate();
        await using var gated = Gated(gate);
        using var client = gated.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var incident = await ReadyIncidentAsync(client, day, [technician]);
        var hold = gate.HoldFirst(incident.Id);

        var dispatch = DispatchAsync(client, incident.Id, Dispatch(technician));
        await hold.Arrived.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);

        // While the dispatch waits between its (passed) final check and its claim, other work takes the technician.
        var competitor = await ExistingWorkAsync(client, technician, day.AddHours(10).AddMinutes(30), day.AddHours(11).AddMinutes(30));
        hold.Release();
        using var response = await dispatch;

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>(Cancellation))!;
        Assert.Equal("Resources not available", problem.Title);
        Assert.Equal(competitor.Visit.Id, ((JsonElement)problem.Extensions["impact"]!)[0].GetProperty("conflictingVisitId").GetGuid());

        // Nothing of the dispatch remains: no work order, visit, assignment, reservation or event of it.
        var current = await GetAsync<IncidentResponse>(client, $"/api/incidents/{incident.Id}");
        Assert.Equal(("ReadyForDispatch", (Guid?)null), (current.Status, current.WorkOrderId));
        Assert.Equal(new DatabaseState(0, 0, 1, 1, 0), await StateAsync(factory, incident.CustomerId, technician));
        Assert.Equal(["IncidentCreated", "IncidentAnalyzed"], await IncidentEventTypesAsync(factory, incident.Id));
        Assert.Equal(1, await AssignmentCreatedEventsAsync(technician));

        // The dispatch's outbox messages (incident.dispatched, assignment.created) were staged and rolled back with it.
        Assert.Equal(0, await OutboxCountAsync(incident.Id));
        Assert.Equal(1, await OutboxAssignmentsCreatedAsync(technician)); // the competitor's only
    }

    [Fact]
    public async Task A_database_failure_while_claiming_leaves_no_partial_dispatch()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var incident = await ReadyIncidentAsync(client, day, [technician]);

        // The assignment insert fails in the database, after the work order, visit and incident rows were sent.
        await using (var failing = await FailAssignmentInsertsAsync(technician))
        {
            using var response = await DispatchAsync(client, incident.Id, Dispatch(technician));
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        var current = await GetAsync<IncidentResponse>(client, $"/api/incidents/{incident.Id}");
        Assert.Equal(("ReadyForDispatch", (Guid?)null), (current.Status, current.WorkOrderId));
        Assert.Equal(new DatabaseState(0, 0, 0, 0, 0), await StateAsync(factory, incident.CustomerId, technician));
        Assert.Equal(["IncidentCreated", "IncidentAnalyzed"], await IncidentEventTypesAsync(factory, incident.Id));
        Assert.Equal(0, await OutboxCountAsync(incident.Id));

        // Nothing was left behind that blocks a later dispatch.
        using var retry = await DispatchAsync(client, incident.Id, Dispatch(technician));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_concurrent_dispatches_of_one_incident_give_one_success_one_conflict_and_one_work_order(bool sameTechnician)
    {
        var gate = new DispatchStagingGate();
        await using var gated = Gated(gate);
        using var client = gated.CreateClient();
        var day = NewDay();
        var first = await CreateTechnicianAsync(client);
        var second = sameTechnician ? first : await CreateTechnicianAsync(client);
        var incident = await ReadyIncidentAsync(client, day, [first, second]);
        gate.Together(2, incident.Id); // both pass the final check and stage before either commits

        var responses = await Task.WhenAll(
            DispatchAsync(client, incident.Id, Dispatch(first)),
            DispatchAsync(client, incident.Id, Dispatch(second)));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        var winner = (await responses.Single(r => r.StatusCode == HttpStatusCode.Created).Content.ReadFromJsonAsync<IncidentDispatchResponse>(Cancellation))!;
        var loser = (await responses.Single(r => r.StatusCode == HttpStatusCode.Conflict).Content.ReadFromJsonAsync<ProblemDetails>(Cancellation))!;
        Assert.Equal("Incident already dispatched", loser.Title);

        var current = await GetAsync<IncidentResponse>(client, $"/api/incidents/{incident.Id}");
        Assert.Equal(("Dispatched", (Guid?)winner.WorkOrderId), (current.Status, current.WorkOrderId));
        Assert.Equal(new DatabaseState(1, 1, 1, 1, 1), await StateAsync(factory, incident.CustomerId, first, second));
        Assert.Equal(["IncidentCreated", "IncidentAnalyzed", "IncidentDispatched"], await IncidentEventTypesAsync(factory, incident.Id));

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Two_incidents_racing_for_one_technician_give_one_success_and_one_409_and_the_loser_stays_ready()
    {
        var gate = new DispatchStagingGate();
        await using var gated = Gated(gate);
        using var client = gated.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var incidentA = await ReadyIncidentAsync(client, day, [technician]);
        var incidentB = await ReadyIncidentAsync(client, day, [technician]);
        gate.Together(2, incidentA.Id, incidentB.Id);

        var responses = await Task.WhenAll(
            DispatchAsync(client, incidentA.Id, Dispatch(technician)),
            DispatchAsync(client, incidentB.Id, Dispatch(technician)));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        var (winner, loser) = responses[0].StatusCode == HttpStatusCode.Created ? (incidentA, incidentB) : (incidentB, incidentA);
        var problem = (await responses.Single(r => r.StatusCode == HttpStatusCode.Conflict).Content.ReadFromJsonAsync<ProblemDetails>(Cancellation))!;
        Assert.Equal("Resources not available", problem.Title);

        Assert.Equal("Dispatched", (await GetAsync<IncidentResponse>(client, $"/api/incidents/{winner.Id}")).Status);
        var losing = await GetAsync<IncidentResponse>(client, $"/api/incidents/{loser.Id}");
        Assert.Equal(("ReadyForDispatch", (Guid?)null), (losing.Status, losing.WorkOrderId));
        Assert.Equal(new DatabaseState(0, 0, 1, 1, 0), await StateAsync(factory, loser.CustomerId, technician));
        Assert.Equal(["IncidentCreated", "IncidentAnalyzed"], await IncidentEventTypesAsync(factory, loser.Id));
        Assert.Equal(1, await ReservationCountAsync(factory, technician, ResourceType.Technician));

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    /// <summary>A second host on the same database whose incident port waits at the gate before staging a dispatch.</summary>
    private WebApplicationFactory<Program> Gated(DispatchStagingGate gate) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var original = services.Single(descriptor => descriptor.ServiceType == typeof(IIncidentWorkOrders));
            services.Remove(original);
            services.AddScoped<IIncidentWorkOrders>(provider => new GatedIncidentWorkOrders(
                (IIncidentWorkOrders)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!), gate));
        }));

    private async Task<int> AssignmentCreatedEventsAsync(Guid technicianId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CrewCallDbContext>();
        return await db.OperationalEvents.CountAsync(
            e => e.EventType == "AssignmentCreated" && EF.Functions.JsonContains(e.PayloadJson, $"{{\"technicianId\":\"{technicianId}\"}}"),
            Cancellation);
    }

    private async Task<int> OutboxCountAsync(Guid aggregateId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().OutboxMessages
            .CountAsync(message => message.AggregateId == aggregateId, Cancellation);
    }

    private async Task<int> OutboxAssignmentsCreatedAsync(Guid technicianId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().OutboxMessages.CountAsync(
            message => message.Type == "assignment.created" && EF.Functions.JsonContains(message.Payload, $"{{\"technicianId\":\"{technicianId}\"}}"),
            Cancellation);
    }

    /// <summary>Makes the database reject inserting an assignment for the technician until disposed.</summary>
    private async Task<IAsyncDisposable> FailAssignmentInsertsAsync(Guid technicianId)
    {
        var name = $"test_fail_assignment_{technicianId:N}";
        await ExecuteAsync($"""
            CREATE FUNCTION scheduling.{name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'forced claim failure'; END $$;
            CREATE TRIGGER {name} BEFORE INSERT ON scheduling.assignments
            FOR EACH ROW WHEN (NEW.technician_id = '{technicianId}') EXECUTE FUNCTION scheduling.{name}();
            """);
        return new Cleanup(() => ExecuteAsync($"DROP TRIGGER {name} ON scheduling.assignments; DROP FUNCTION scheduling.{name}();"));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
#pragma warning disable EF1002 // test-only DDL built from a Guid
        await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().Database.ExecuteSqlRawAsync(sql, Cancellation);
#pragma warning restore EF1002
    }

    private sealed class Cleanup(Func<Task> cleanup) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await cleanup();
    }
}

/// <summary>Holds dispatches of chosen incidents before their WorkOrders staging (after the final check passed).</summary>
public sealed class DispatchStagingGate
{
    private readonly ConcurrentDictionary<Guid, Gate> _gates = new();

    /// <summary>The first staging of the incident waits until <see cref="Gate.Release"/>.</summary>
    public Gate HoldFirst(Guid incidentId) => _gates[incidentId] = new Gate(int.MaxValue);

    /// <summary>The first <paramref name="callers"/> stagings of these incidents wait for each other, then go together.</summary>
    public void Together(int callers, params Guid[] incidentIds)
    {
        var gate = new Gate(callers);
        foreach (var incidentId in incidentIds)
        {
            _gates[incidentId] = gate;
        }
    }

    internal Task ArriveAsync(Guid incidentId, CancellationToken cancellationToken) =>
        _gates.TryGetValue(incidentId, out var gate) ? gate.ArriveAsync(cancellationToken) : Task.CompletedTask;

    public sealed class Gate(int callers)
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = callers;

        public Task Arrived => _arrived.Task;

        public void Release() => _open.TrySetResult();

        internal Task ArriveAsync(CancellationToken cancellationToken)
        {
            _arrived.TrySetResult();
            if (Interlocked.Decrement(ref _remaining) <= 0)
            {
                _open.TrySetResult();
            }

            return _open.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
    }
}

internal sealed class GatedIncidentWorkOrders(IIncidentWorkOrders inner, DispatchStagingGate gate) : IIncidentWorkOrders
{
    public Task<IncidentSchedulingInfo?> GetIncidentAsync(Guid incidentId, CancellationToken cancellationToken) =>
        inner.GetIncidentAsync(incidentId, cancellationToken);

    public Task<IncidentStateChange> BeginAnalysisAsync(Guid incidentId, CancellationToken cancellationToken) =>
        inner.BeginAnalysisAsync(incidentId, cancellationToken);

    public Task<IncidentStateChange> CompleteAnalysisAsync(IncidentAnalysisSummary analysis, CancellationToken cancellationToken) =>
        inner.CompleteAnalysisAsync(analysis, cancellationToken);

    public async Task<IncidentDispatchStaging> StageDispatchAsync(IncidentDispatchPlan plan, CancellationToken cancellationToken)
    {
        await gate.ArriveAsync(plan.IncidentId, cancellationToken);
        return await inner.StageDispatchAsync(plan, cancellationToken);
    }

    public Task<IReadOnlyDictionary<Guid, VisitPlanInfo>> GetVisitPlansAsync(IReadOnlyCollection<Guid> visitIds, CancellationToken cancellationToken) =>
        inner.GetVisitPlansAsync(visitIds, cancellationToken);

    public Task<bool> StageVisitRescheduleAsync(VisitReschedulePlan plan, CancellationToken cancellationToken) =>
        inner.StageVisitRescheduleAsync(plan, cancellationToken);
}
