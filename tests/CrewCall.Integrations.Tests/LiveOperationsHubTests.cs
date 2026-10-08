using CrewCall.Contracts.Integration;
using CrewCall.Contracts.Live;
using CrewCall.Integrations.Live;
using Microsoft.AspNetCore.SignalR;
using Xunit;
using static CrewCall.Integrations.Tests.LiveTestKit;

namespace CrewCall.Integrations.Tests;

/// <summary>The real hub and SignalR publisher, with real SignalR clients over a TestServer (ADR-0015).</summary>
public sealed class LiveOperationsHubTests : IAsyncLifetime
{
    private LiveHubHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await LiveHubHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_client_that_subscribed_to_all_receives_every_message_through_ReceiveOperation()
    {
        await using var client = await _host.ConnectAsync();
        await client.InvokeAsync(LiveOperationsHubContract.SubscribeAll);
        var message = Message(siteId: Guid.NewGuid());

        await _host.Publisher.PublishAsync(message, Cancellation);

        var received = await client.NextAsync();
        Assert.Equal(message.Message, received); // the whole (small) message: type, entity, time, ids
    }

    [Fact]
    public async Task A_site_group_receives_its_sites_messages_and_not_another_sites()
    {
        var (siteA, siteB) = (Guid.NewGuid(), Guid.NewGuid());
        await using var clientA = await _host.ConnectAsync();
        await using var clientB = await _host.ConnectAsync();
        await clientA.InvokeAsync(LiveOperationsHubContract.SubscribeSite, siteA);
        await clientB.InvokeAsync(LiveOperationsHubContract.SubscribeSite, siteB);

        var forA = Message(siteId: siteA);
        await _host.Publisher.PublishAsync(forA, Cancellation);
        var forB = Message(siteId: siteB);
        await _host.Publisher.PublishAsync(forB, Cancellation);

        await clientA.AssertNextIsAsync(forA);
        await clientB.AssertNextIsAsync(forB); // not forA, which was sent first
    }

    [Fact]
    public async Task Technician_and_incident_groups_receive_their_messages()
    {
        var (technicianId, incidentId) = (Guid.NewGuid(), Guid.NewGuid());
        await using var technician = await _host.ConnectAsync();
        await using var incident = await _host.ConnectAsync();
        await technician.InvokeAsync(LiveOperationsHubContract.SubscribeTechnician, technicianId);
        await incident.InvokeAsync(LiveOperationsHubContract.SubscribeIncident, incidentId);

        var forTechnician = Message(technicianId: technicianId);
        var forIncident = Message(incidentId: incidentId);
        await _host.Publisher.PublishAsync(forTechnician, Cancellation);
        await _host.Publisher.PublishAsync(forIncident, Cancellation);

        await technician.AssertNextIsAsync(forTechnician);
        await incident.AssertNextIsAsync(forIncident);
    }

    [Fact]
    public async Task After_unsubscribing_a_client_no_longer_receives_the_groups_messages()
    {
        var siteId = Guid.NewGuid();
        await using var client = await _host.ConnectAsync();
        await client.InvokeAsync(LiveOperationsHubContract.SubscribeSite, siteId);
        await client.InvokeAsync(LiveOperationsHubContract.SubscribeAll);
        var first = Message(siteId: siteId);
        await _host.Publisher.PublishAsync(first, Cancellation);
        await client.AssertNextIsAsync(first);
        // Subscribed to all and the site: the same message arrives once per matching group, and the client deduplicates.
        await client.AssertNextIsAsync(first);

        await client.InvokeAsync(LiveOperationsHubContract.UnsubscribeSite, siteId);
        await client.InvokeAsync(LiveOperationsHubContract.UnsubscribeAll);
        await _host.Publisher.PublishAsync(Message(siteId: siteId), Cancellation);

        // Subscribed to an unrelated technician only: the next message it sees is the sentinel.
        var technicianId = Guid.NewGuid();
        await client.InvokeAsync(LiveOperationsHubContract.SubscribeTechnician, technicianId);
        var sentinel = Message(technicianId: technicianId);
        await _host.Publisher.PublishAsync(sentinel, Cancellation);
        await client.AssertNextIsAsync(sentinel);
    }

    [Fact]
    public async Task An_incident_dispatch_reaches_all_the_incident_and_the_technician_but_not_an_unrelated_technician()
    {
        var dispatched = new IncidentDispatchedIntegrationEvent(
            Guid.NewGuid(), OccurredAt, null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var message = new LiveDelivery(
            LiveOperationMapper.Map(Envelope(dispatched), dispatched), LiveOperationGroups.For(dispatched, new LiveRoutingContext(Guid.NewGuid(), [])));
        var unrelatedTechnician = Guid.NewGuid();

        await using var a = await _host.ConnectAsync();
        await using var b = await _host.ConnectAsync();
        await using var c = await _host.ConnectAsync();
        await using var d = await _host.ConnectAsync();
        await a.InvokeAsync(LiveOperationsHubContract.SubscribeAll);
        await b.InvokeAsync(LiveOperationsHubContract.SubscribeIncident, dispatched.IncidentId);
        await c.InvokeAsync(LiveOperationsHubContract.SubscribeTechnician, dispatched.TechnicianId);
        await d.InvokeAsync(LiveOperationsHubContract.SubscribeTechnician, unrelatedTechnician);

        await _host.Publisher.PublishAsync(message, Cancellation);
        var sentinel = Message(technicianId: unrelatedTechnician);
        await _host.Publisher.PublishAsync(sentinel, Cancellation);

        var receivedByA = await a.NextAsync();
        Assert.Equal(("incident.dispatched", message.MessageId), (receivedByA.Type, receivedByA.MessageId));
        await b.AssertNextIsAsync(message);
        await c.AssertNextIsAsync(message);
        await d.AssertNextIsAsync(sentinel); // D never saw the dispatch
    }

    [Fact]
    public async Task Invalid_ids_and_raw_group_names_are_refused()
    {
        await using var client = await _host.ConnectAsync();

        var empty = await Assert.ThrowsAsync<HubException>(() => client.InvokeAsync(LiveOperationsHubContract.SubscribeSite, Guid.Empty));
        Assert.Contains("site id is required", empty.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<HubException>(() => client.InvokeAsync(LiveOperationsHubContract.SubscribeTechnician, "not-a-guid"));
        await Assert.ThrowsAsync<HubException>(() => client.InvokeAsync(LiveOperationsHubContract.SubscribeSite, "all"));
        await Assert.ThrowsAsync<HubException>(() => client.InvokeAsync("JoinGroup", "site:anything"));
        await Assert.ThrowsAsync<HubException>(() => client.InvokeAsync("AddToGroupAsync", client.Connection.ConnectionId, "all"));

        // Nothing was joined: the client only sees what it subscribes to afterwards.
        var technicianId = Guid.NewGuid();
        await _host.Publisher.PublishAsync(Message(siteId: Guid.NewGuid()), Cancellation);
        await client.InvokeAsync(LiveOperationsHubContract.SubscribeTechnician, technicianId);
        var sentinel = Message(technicianId: technicianId);
        await _host.Publisher.PublishAsync(sentinel, Cancellation);
        await client.AssertNextIsAsync(sentinel);
    }

    [Fact]
    public async Task Broadcasting_with_no_client_connected_is_a_success()
    {
        await _host.Publisher.PublishAsync(Message(siteId: Guid.NewGuid(), technicianId: Guid.NewGuid()), Cancellation);

        Assert.Contains(_host.Logs, line => line.StartsWith("Live visit.work.completed", StringComparison.Ordinal) && line.Contains("published", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Connections_and_subscriptions_are_logged()
    {
        var siteId = Guid.NewGuid();
        await using (var client = await _host.ConnectAsync())
        {
            await client.InvokeAsync(LiveOperationsHubContract.SubscribeSite, siteId);
        }

        await Wait.UntilAsync(() => Task.FromResult(_host.Logs.Any(line => line.Contains("closed", StringComparison.Ordinal))), "the close");
        Assert.Contains(_host.Logs, line => line.Contains("opened", StringComparison.Ordinal));
        Assert.Contains(_host.Logs, line => line.Contains($"subscribed to site {siteId}", StringComparison.Ordinal));
    }
}
