extern alias web;

using CrewCall.Contracts.Live;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using web::CrewCall.Web.Live;
using Xunit;
using static CrewCall.Integrations.Tests.LiveTestKit;

namespace CrewCall.Integrations.Tests;

/// <summary>
/// CrewCall.Web's SignalR client against the real hub on Kestrel (WebSockets): automatic reconnect with its groups
/// restored, MessageId deduplication, and the bounded list of recent messages (ADR-0015).
/// </summary>
public sealed class LiveOperationsWebClientTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static LiveOperationsClient Client(Uri hubUrl) =>
        new(hubUrl, NullLogger<LiveOperationsClient>.Instance, retryPolicy: new FastRetry());

    [Fact]
    public void The_hub_address_comes_from_configuration_or_service_discovery_never_a_hard_coded_url()
    {
        static Uri? Resolve(params (string Key, string Value)[] settings) => LiveHubAddress.Resolve(
            new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => KeyValuePair.Create(s.Key, (string?)s.Value))).Build());

        Assert.Null(Resolve());
        Assert.Equal(new Uri("http://localhost:5100/hubs/live-operations"),
            Resolve(("services:crewcall-integrations:http:0", "http://localhost:5100")));
        Assert.Equal(new Uri("https://localhost:7100/hubs/live-operations"),
            Resolve(("services:crewcall-integrations:http:0", "http://localhost:5100"), ("services:crewcall-integrations:https:0", "https://localhost:7100")));
        Assert.Equal(new Uri("https://live.example/hubs/live-operations"),
            Resolve(("services:crewcall-integrations:https:0", "https://localhost:7100"), ("LiveOperations:HubUrl", "https://live.example/hubs/live-operations")));
    }

    [Fact]
    public async Task After_the_hub_restarts_the_client_reconnects_and_its_groups_are_restored()
    {
        var port = FreePort();
        var siteId = Guid.NewGuid();
        var first = await LiveHubHost.StartAsync(port);
        await using var client = Client(first.HubUrl);
        await client.SubscribeAsync(LiveSubscription.Site(siteId), Cancellation);
        await client.StartAsync(Cancellation);
        Assert.Equal(LiveConnectionState.Connected, client.State);

        var before = Message(siteId: siteId);
        await first.Publisher.PublishAsync(before, Cancellation);
        await Wait.UntilAsync(() => Task.FromResult(client.Recent.Any(m => m.MessageId == before.MessageId)), "the first message");

        // The hub goes away: the client notices and keeps trying.
        await first.DisposeAsync();
        await Wait.UntilAsync(() => Task.FromResult(client.State == LiveConnectionState.Reconnecting), "Reconnecting");

        // A new hub process knows nothing about the old connection's groups; the client joins them again.
        await using var second = await LiveHubHost.StartAsync(port);
        await Wait.UntilAsync(() => Task.FromResult(client.State == LiveConnectionState.Connected), "Connected", TimeSpan.FromSeconds(30));
        var after = Message(siteId: siteId);
        await second.Publisher.PublishAsync(after, Cancellation);

        await Wait.UntilAsync(() => Task.FromResult(client.Recent.Any(m => m.MessageId == after.MessageId)), "the message after the reconnect");
        Assert.Equal([LiveSubscription.Site(siteId)], client.Subscriptions);
    }

    [Fact]
    public async Task A_duplicate_message_id_is_shown_once_and_only_the_latest_fifty_messages_are_kept()
    {
        await using var host = await LiveHubHost.StartAsync(FreePort());
        await using var client = Client(host.HubUrl);
        await client.SubscribeAsync(LiveSubscription.All, Cancellation);
        await client.StartAsync(Cancellation);

        var messages = Enumerable.Range(0, 55).Select(_ => Message()).ToList();
        foreach (var message in messages)
        {
            await host.Publisher.PublishAsync(message, Cancellation);
        }

        // At-least-once: the same MessageId again (e.g. redelivered after a crash before the inbox write).
        await host.Publisher.PublishAsync(messages[^1], Cancellation);
        var end = Message();
        await host.Publisher.PublishAsync(end, Cancellation);

        await Wait.UntilAsync(() => Task.FromResult(client.Recent.FirstOrDefault()?.MessageId == end.MessageId), "the last message");
        Assert.Equal(1, client.DuplicatesIgnored);
        Assert.Equal(LiveOperationsClient.Capacity, client.Recent.Count);
        Assert.Equal(
            messages.Skip(6).Select(m => m.MessageId).Append(end.MessageId).Reverse(),
            client.Recent.Select(m => m.MessageId));
    }

    [Fact]
    public async Task Unsubscribing_stops_delivery_and_is_not_restored()
    {
        await using var host = await LiveHubHost.StartAsync(FreePort());
        await using var client = Client(host.HubUrl);
        var (siteId, technicianId) = (Guid.NewGuid(), Guid.NewGuid());
        await client.StartAsync(Cancellation);
        await client.SubscribeAsync(LiveSubscription.Site(siteId), Cancellation);
        await client.SubscribeAsync(LiveSubscription.Technician(technicianId), Cancellation);

        await client.UnsubscribeAsync(LiveSubscription.Site(siteId), Cancellation);
        await host.Publisher.PublishAsync(Message(siteId: siteId), Cancellation);
        var sentinel = Message(technicianId: technicianId);
        await host.Publisher.PublishAsync(sentinel, Cancellation);

        await Wait.UntilAsync(() => Task.FromResult(client.Recent.Count > 0), "the sentinel");
        Assert.Equal([sentinel.MessageId], client.Recent.Select(m => m.MessageId));
        Assert.Equal([LiveSubscription.Technician(technicianId)], client.Subscriptions);
    }

    [Fact]
    public async Task Without_a_reachable_hub_the_client_reports_disconnected_instead_of_failing()
    {
        await using var client = Client(new Uri($"http://127.0.0.1:{FreePort()}{LiveOperationsHubContract.Path}"));

        await client.StartAsync(Cancellation);

        Assert.Equal(LiveConnectionState.Disconnected, client.State);
        Assert.False(string.IsNullOrEmpty(client.LastError));
    }

    private sealed class FastRetry : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            retryContext.ElapsedTime < TimeSpan.FromSeconds(30) ? TimeSpan.FromMilliseconds(200) : null;
    }
}
