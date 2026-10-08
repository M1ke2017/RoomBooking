using CrewCall.Contracts.Integration;
using Xunit;
using static CrewCall.Reporting.Tests.Events;

namespace CrewCall.Reporting.Tests;

/// <summary>
/// Determinism (ADR-0016): the same logical history gives the same read model, whether events arrive in order, out of
/// order or more than once, and when the projections are rebuilt from the same envelopes.
/// </summary>
[Collection(Sequential.Name)]
public sealed class OutOfOrderAndReplayTests(ReportingInfrastructure infrastructure) : IAsyncLifetime
{
    private readonly ManualClock _clock = new(Day1.AddDays(30));
    private ProjectionHarness _harness = null!;

    public async ValueTask InitializeAsync()
    {
        await infrastructure.ResetAsync();
        _harness = new ProjectionHarness(infrastructure, _clock);
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private async Task<string> ProjectFromEmptyAsync(IEnumerable<IntegrationEventEnvelope> envelopes)
    {
        await _harness.ResetAsync(includeInbox: true);
        await _harness.ProcessAllAsync(envelopes);
        return await _harness.SnapshotAsync();
    }

    [Fact]
    public async Task Scenario_A_a_completion_that_arrives_before_its_assignment_is_attributed_when_the_assignment_arrives()
    {
        var (visitId, technicianId) = (Guid.NewGuid(), Guid.NewGuid());
        var visit = Envelope(VisitCreated(visitId, Day1.AddHours(9), TimeSpan.FromHours(2)));
        var assignment = Envelope(AssignmentCreated(Guid.NewGuid(), visitId, technicianId, Day1.AddHours(7)));
        var completion = Envelope(WorkCompleted(visitId, Day1.AddHours(11), 15m, 120m, 20m));

        var inOrder = await ProjectFromEmptyAsync([visit, assignment, completion]);

        // Out of order: the completion first. Nothing is lost: the visit keeps its durations, without a technician yet.
        await _harness.ResetAsync();
        await _harness.ProcessAllAsync([visit, completion]);
        var waiting = (await _harness.VisitAsync(visitId))!;
        Assert.Equal(((Guid?)null, 100m), (waiting.TechnicianId, waiting.ActualNetWorkMinutes));
        Assert.Empty(await _harness.DaysAsync(technicianId));

        // The assignment arrives: the visit is attributed and the technician's day is corrected deterministically.
        await _harness.ProcessAsync(assignment);
        Assert.Equal(technicianId, (await _harness.VisitAsync(visitId))!.TechnicianId);
        var day = Assert.Single(await _harness.DaysAsync(technicianId));
        Assert.Equal((1, 1, 15m, 120m, 20m, 100m), (day.CompletedVisits, day.AssignmentCount, day.TravelMinutes, day.GrossWorkMinutes, day.PauseMinutes, day.NetWorkMinutes));
        Assert.Equal(inOrder, await _harness.SnapshotAsync());
    }

    [Fact]
    public async Task Scenario_B_a_reassignment_around_the_completion_attributes_the_work_once_to_the_new_technician_in_every_order()
    {
        var (visitId, first, second, previousTechnician, newTechnician) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        IntegrationEventEnvelope[] history =
        [
            Envelope(AssignmentCreated(first, visitId, previousTechnician, Day1.AddHours(7))),
            Envelope(AssignmentCreated(second, visitId, newTechnician, Day1.AddHours(10))),
            Envelope(AssignmentReplaced(first, second, visitId, Day1.AddHours(10))),
            Envelope(WorkCompleted(visitId, Day1.AddHours(12), 10m, 60m, 0m))
        ];
        await _harness.ProcessAsync(VisitCreated(visitId, Day1.AddHours(11), TimeSpan.FromHours(1)));

        var expected = await ProjectFromEmptyAsync(history);
        var previous = Assert.Single(await _harness.DaysAsync(previousTechnician));
        var current = Assert.Single(await _harness.DaysAsync(newTechnician));
        Assert.Equal((1, 0, 0m), (previous.AssignmentCount, previous.CompletedVisits, previous.NetWorkMinutes)); // assigned, then replaced
        Assert.Equal((1, 1, 60m), (current.AssignmentCount, current.CompletedVisits, current.NetWorkMinutes));    // did the work

        foreach (var order in Permutations(history))
        {
            Assert.Equal(expected, await ProjectFromEmptyAsync(order));
        }
    }

    [Fact]
    public async Task Duplicated_deliveries_mixed_into_the_history_change_nothing()
    {
        var history = RealisticHistory();
        var expected = await ProjectFromEmptyAsync(history);

        var withDuplicates = history.SelectMany((envelope, index) => index % 3 == 0 ? [envelope, envelope] : new[] { envelope }).Concat(history.Take(5));

        Assert.Equal(expected, await ProjectFromEmptyAsync(withDuplicates));
    }

    [Fact]
    public async Task Replaying_the_same_envelopes_into_an_empty_read_model_gives_an_identical_result_every_time()
    {
        var history = RealisticHistory();
        var first = await ProjectFromEmptyAsync(history);
        var technician1 = await _harness.DaysAsync(new Guid("00000000-0000-0000-0000-000000000001"));
        Assert.Equal([(1, 2, 50m)], technician1.Select(d => (d.CompletedVisits, d.AssignmentCount, d.NetWorkMinutes))); // V1 done; A1, A4 given
        var technician3 = await _harness.DaysAsync(new Guid("00000000-0000-0000-0000-000000000003"));
        Assert.Equal([(1, 1, 120m)], technician3.Select(d => (d.CompletedVisits, d.AssignmentCount, d.NetWorkMinutes))); // V2 after the reassignment

        for (var replay = 0; replay < 3; replay++)
        {
            Assert.Equal(first, await ProjectFromEmptyAsync(history));
        }

        // Rebuilt in reverse order the result is still the same: no projection depends on arrival order.
        Assert.Equal(first, await ProjectFromEmptyAsync(history.AsEnumerable().Reverse()));
    }

    [Fact]
    public async Task Resetting_without_the_inbox_keeps_the_inbox_so_a_redelivery_is_still_recognized()
    {
        var history = RealisticHistory();
        await ProjectFromEmptyAsync(history);

        await _harness.ResetAsync(includeInbox: false);
        await _harness.ProcessAllAsync(history);

        // Every message was seen before: the projections stay empty until the inbox is reset too (a deliberate step).
        Assert.Contains("\"Days\":[]", await _harness.SnapshotAsync());
    }

    /// <summary>Three technicians over two days: assignments, a reassignment, a cancellation, an incident, completions.</summary>
    private static IntegrationEventEnvelope[] RealisticHistory()
    {
        Guid T(int n) => new($"00000000-0000-0000-0000-00000000000{n}");
        Guid V(int n) => new($"00000000-0000-0000-0001-00000000000{n}");
        Guid A(int n) => new($"00000000-0000-0000-0002-00000000000{n}");
        var day2 = Day1.AddDays(1);
        return
        [
            Envelope(VisitCreated(V(1), Day1.AddHours(9), TimeSpan.FromHours(1))),
            Envelope(VisitCreated(V(2), Day1.AddHours(13), TimeSpan.FromHours(2))),
            Envelope(VisitCreated(V(3), day2.AddHours(8), TimeSpan.FromHours(1))),
            Envelope(AssignmentCreated(A(1), V(1), T(1), Day1.AddHours(6))),
            Envelope(AssignmentCreated(A(2), V(2), T(2), Day1.AddHours(6))),
            Envelope(AssignmentCreated(A(3), V(2), T(3), Day1.AddHours(11))),
            Envelope(AssignmentReplaced(A(2), A(3), V(2), Day1.AddHours(11))),
            Envelope(AssignmentCreated(A(4), V(3), T(1), Day1.AddHours(16))),
            Envelope(AssignmentCancelled(A(4), V(3), day2.AddHours(7))),
            Envelope(IncidentDispatched(Guid.NewGuid(), V(3), A(5), T(2), day2.AddHours(7))),
            Envelope(AssignmentCreated(A(5), V(3), T(2), day2.AddHours(7))),
            Envelope(StatusChanged(V(1), "Planned", "InProgress", Day1.AddHours(9))),
            Envelope(WorkCompleted(V(1), Day1.AddHours(10), 20m, 55m, 5m)),
            Envelope(StatusChanged(V(1), "InProgress", "Completed", Day1.AddHours(10))),
            Envelope(WorkCompleted(V(2), Day1.AddHours(15), null, 130m, 10m)),
            Envelope(StatusChanged(V(3), "Planned", "Cancelled", day2.AddHours(9)))
        ];
    }

    private static IEnumerable<IntegrationEventEnvelope[]> Permutations(IntegrationEventEnvelope[] items) =>
        items.Length <= 1
            ? [items]
            : items.SelectMany((item, index) =>
                Permutations([.. items.Take(index), .. items.Skip(index + 1)]).Select(rest => (IntegrationEventEnvelope[])[item, .. rest]));
}
