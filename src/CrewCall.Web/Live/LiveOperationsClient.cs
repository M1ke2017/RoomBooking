using CrewCall.Contracts.Live;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;

namespace CrewCall.Web.Live;

public enum LiveConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting
}

public enum LiveSubscriptionKind
{
    All,
    Site,
    Technician,
    Incident
}

/// <summary>A group the client wants to hear: everything, or one site, technician or incident.</summary>
public readonly record struct LiveSubscription(LiveSubscriptionKind Kind, Guid? Id)
{
    public static LiveSubscription All { get; } = new(LiveSubscriptionKind.All, null);

    public static LiveSubscription Site(Guid id) => new(LiveSubscriptionKind.Site, id);

    public static LiveSubscription Technician(Guid id) => new(LiveSubscriptionKind.Technician, id);

    public static LiveSubscription Incident(Guid id) => new(LiveSubscriptionKind.Incident, id);

    public override string ToString() => Id is { } id ? $"{Kind.ToString().ToLowerInvariant()}:{id}" : "all";
}

/// <summary>
/// A SignalR client of the live operations hub (ADR-0015), for the developer page /live.
/// - Reconnects automatically and, after a reconnect, joins its groups again: a new connection starts with none.
/// - Delivery is at-least-once, so a message seen before (same MessageId) is ignored.
/// - Keeps the last <see cref="Capacity"/> messages for display only. They are change hints, not state: a real screen
///   refreshes the affected data from the REST API instead of building it from messages.
/// </summary>
public sealed class LiveOperationsClient : IAsyncDisposable
{
    public const int Capacity = 50;

    // MessageIds remembered for deduplication; more than the display, so a late duplicate is still recognized.
    private const int SeenCapacity = 1000;

    private readonly HubConnection _connection;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly LinkedList<LiveOperationMessage> _recent = new();
    private readonly HashSet<Guid> _seen = [];
    private readonly Queue<Guid> _seenOrder = new();
    private readonly HashSet<LiveSubscription> _subscriptions = [];
    private LiveConnectionState _state = LiveConnectionState.Disconnected;
    private int _duplicatesIgnored;

    public LiveOperationsClient(
        Uri hubUrl, ILogger<LiveOperationsClient> logger, Action<HttpConnectionOptions>? configure = null, IRetryPolicy? retryPolicy = null)
    {
        HubUrl = hubUrl;
        _logger = logger;

        var builder = new HubConnectionBuilder().WithUrl(hubUrl, options => configure?.Invoke(options));
        _connection = (retryPolicy is null ? builder.WithAutomaticReconnect() : builder.WithAutomaticReconnect(retryPolicy)).Build();

        _connection.On<LiveOperationMessage>(LiveOperationsHubContract.ReceiveOperation, OnReceived);
        _connection.Reconnecting += error =>
        {
            _logger.LogWarning(error, "Live operations connection lost; reconnecting.");
            SetState(LiveConnectionState.Reconnecting);
            return Task.CompletedTask;
        };
        _connection.Reconnected += async _ =>
        {
            // The server sees a new connection without groups: join them again before reporting Connected.
            try
            {
                await RestoreSubscriptionsAsync(CancellationToken.None);
                _logger.LogInformation("Live operations connection restored with {Count} subscriptions.", Subscriptions.Count);
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                _logger.LogWarning(exception, "Live operations subscriptions could not be restored after reconnecting.");
            }

            SetState(LiveConnectionState.Connected);
        };
        _connection.Closed += error =>
        {
            _logger.LogWarning(error, "Live operations connection closed.");
            SetState(LiveConnectionState.Disconnected);
            return Task.CompletedTask;
        };
    }

    public Uri HubUrl { get; }

    /// <summary>Raised after the state, the subscriptions or the recent messages changed (on a background thread).</summary>
    public event Action? Changed;

    /// <summary>Raised for each new (not duplicate) message.</summary>
    public event Action<LiveOperationMessage>? Received;

    public LiveConnectionState State
    {
        get { lock (_gate) { return _state; } }
    }

    public string? LastError { get; private set; }

    public int DuplicatesIgnored
    {
        get { lock (_gate) { return _duplicatesIgnored; } }
    }

    /// <summary>The most recent messages, newest first.</summary>
    public IReadOnlyList<LiveOperationMessage> Recent
    {
        get { lock (_gate) { return [.. _recent]; } }
    }

    public IReadOnlyList<LiveSubscription> Subscriptions
    {
        get { lock (_gate) { return [.. _subscriptions]; } }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        SetState(LiveConnectionState.Connecting);
        try
        {
            await _connection.StartAsync(cancellationToken);
            LastError = null;
            await RestoreSubscriptionsAsync(cancellationToken);
            SetState(LiveConnectionState.Connected);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = exception.Message;
            _logger.LogWarning(exception, "Could not connect to the live operations hub at {HubUrl}.", HubUrl);
            SetState(LiveConnectionState.Disconnected);
        }
    }

    /// <summary>Joins a group now when connected, and again after every reconnect.</summary>
    public async Task SubscribeAsync(LiveSubscription subscription, CancellationToken cancellationToken)
    {
        if (_connection.State == HubConnectionState.Connected)
        {
            await InvokeAsync("Subscribe", subscription, cancellationToken);
        }

        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        Changed?.Invoke();
    }

    public async Task UnsubscribeAsync(LiveSubscription subscription, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }

        if (_connection.State == HubConnectionState.Connected)
        {
            await InvokeAsync("Unsubscribe", subscription, cancellationToken);
        }

        Changed?.Invoke();
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private async Task RestoreSubscriptionsAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in Subscriptions)
        {
            await InvokeAsync("Subscribe", subscription, cancellationToken);
        }
    }

    // Typed hub methods: the group name is built on the server, the client only sends the id.
    private Task InvokeAsync(string verb, LiveSubscription subscription, CancellationToken cancellationToken) =>
        subscription.Id is { } id
            ? _connection.InvokeAsync($"{verb}{subscription.Kind}", id, cancellationToken)
            : _connection.InvokeAsync($"{verb}{subscription.Kind}", cancellationToken);

    private void OnReceived(LiveOperationMessage message)
    {
        lock (_gate)
        {
            if (!_seen.Add(message.MessageId))
            {
                _duplicatesIgnored++;
                return;
            }

            _seenOrder.Enqueue(message.MessageId);
            if (_seenOrder.Count > SeenCapacity)
            {
                _seen.Remove(_seenOrder.Dequeue());
            }

            _recent.AddFirst(message);
            if (_recent.Count > Capacity)
            {
                _recent.RemoveLast();
            }
        }

        Received?.Invoke(message);
        Changed?.Invoke();
    }

    private void SetState(LiveConnectionState state)
    {
        lock (_gate)
        {
            _state = state;
        }

        Changed?.Invoke();
    }
}
