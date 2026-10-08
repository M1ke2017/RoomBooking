using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using CrewCall.Contracts.Integration;
using CrewCall.Contracts.Live;
using CrewCall.Integrations.Live;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CrewCall.Integrations.Tests;

internal static class LiveTestKit
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>The envelope the publisher would send for <paramref name="integrationEvent"/> (MessageId = EventId).</summary>
    public static IntegrationEventEnvelope Envelope(IIntegrationEvent integrationEvent)
    {
        var descriptor = IntegrationEventCatalog.Describe(integrationEvent);
        using var payload = JsonDocument.Parse(IntegrationEventCatalog.SerializePayload(integrationEvent));
        return new IntegrationEventEnvelope(
            integrationEvent.EventId, descriptor.Type, descriptor.Version, integrationEvent.OccurredAtUtc, integrationEvent.CorrelationId,
            payload.RootElement.Clone());
    }

    public static readonly DateTimeOffset OccurredAt = new(2038, 6, 1, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A live message about a visit, routed to the given site, technician and incident (any may be null).</summary>
    public static LiveOperationMessage Message(Guid? siteId = null, Guid? technicianId = null, Guid? incidentId = null)
    {
        var related = new List<LiveEntityReference> { new(LiveEntityTypes.Visit, Guid.NewGuid()) };
        if (siteId is { } site) related.Add(new(LiveEntityTypes.Site, site));
        if (technicianId is { } technician) related.Add(new(LiveEntityTypes.Technician, technician));
        if (incidentId is { } incident) related.Add(new(LiveEntityTypes.Incident, incident));
        return new LiveOperationMessage(
            Guid.NewGuid(), LiveOperationTypes.VisitWorkCompleted, OccurredAt, LiveEntityTypes.Visit, related[0].EntityId,
            LiveOperationActions.Completed, null, related, null);
    }

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>
/// The real hub and the real SignalR publisher in a minimal host: on a TestServer (long polling), or on Kestrel on a fixed
/// port (WebSockets; can be stopped and started again to force client reconnects).
/// </summary>
public sealed class LiveHubHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private LiveHubHost(WebApplication app, Uri baseAddress, Func<HttpMessageHandler>? handler, ConcurrentQueue<string> logs)
    {
        _app = app;
        Logs = logs;
        HubUrl = new Uri(baseAddress, LiveOperationsHubContract.Path);
        Handler = handler;
    }

    public Uri HubUrl { get; }

    /// <summary>For a TestServer host: the in-memory handler clients must use.</summary>
    public Func<HttpMessageHandler>? Handler { get; }

    public ILiveOperationsPublisher Publisher => _app.Services.GetRequiredService<ILiveOperationsPublisher>();

    /// <summary>Everything the host logged (Information and above), formatted.</summary>
    public ConcurrentQueue<string> Logs { get; }

    public static async Task<LiveHubHost> StartAsync(int? kestrelPort = null)
    {
        var builder = WebApplication.CreateBuilder();
        var logs = new ConcurrentQueue<string>();
        builder.Logging.ClearProviders().AddProvider(new QueueLoggerProvider(logs));
        if (kestrelPort is { } port)
        {
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        }
        else
        {
            builder.WebHost.UseTestServer();
        }

        builder.Services.AddSignalR();
        builder.Services.AddSingleton<ILiveOperationsPublisher, SignalRLiveOperationsPublisher>();
        var app = builder.Build();
        app.MapHub<LiveOperationsHub>(LiveOperationsHubContract.Path);
        await app.StartAsync(TestContext.Current.CancellationToken);

        return kestrelPort is { } kestrel
            ? new LiveHubHost(app, new Uri($"http://127.0.0.1:{kestrel}"), null, logs)
            : new LiveHubHost(app, new Uri("http://localhost"), () => app.GetTestServer().CreateHandler(), logs);
    }

    public Task<LiveHubClient> ConnectAsync() => LiveHubClient.ConnectAsync(HubUrl, Handler);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed class QueueLoggerProvider(ConcurrentQueue<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new QueueLogger(logs);

        public void Dispose()
        {
        }
    }

    private sealed class QueueLogger(ConcurrentQueue<string> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            logs.Enqueue(formatter(state, exception));
    }
}

/// <summary>A raw SignalR test client: records every ReceiveOperation in order.</summary>
public sealed class LiveHubClient : IAsyncDisposable
{
    private readonly Channel<LiveOperationMessage> _received = Channel.CreateUnbounded<LiveOperationMessage>();

    private LiveHubClient(HubConnection connection)
    {
        Connection = connection;
        Connection.On<LiveOperationMessage>(LiveOperationsHubContract.ReceiveOperation, message => _received.Writer.TryWrite(message));
    }

    public HubConnection Connection { get; }

    public static async Task<LiveHubClient> ConnectAsync(Uri hubUrl, Func<HttpMessageHandler>? handler)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                if (handler is not null)
                {
                    // TestServer: in-memory HTTP, so long polling.
                    options.HttpMessageHandlerFactory = _ => handler();
                    options.Transports = HttpTransportType.LongPolling;
                }
            })
            .Build();
        var client = new LiveHubClient(connection);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        return client;
    }

    public Task InvokeAsync(string method, params object?[] arguments) =>
        Connection.InvokeCoreAsync(method, arguments, TestContext.Current.CancellationToken);

    public async Task<LiveOperationMessage> NextAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(LiveTestKit.Timeout);
        try
        {
            return await _received.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("No live message arrived.");
        }
    }

    /// <summary>
    /// Asserts that the next message this client receives is <paramref name="sentinel"/>: SignalR keeps the order per
    /// connection, so anything sent to this client before the sentinel would arrive first.
    /// </summary>
    public async Task AssertNextIsAsync(LiveOperationMessage sentinel) => Assert.Equal(sentinel.MessageId, (await NextAsync()).MessageId);

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

/// <summary>Records live messages instead of broadcasting them; fails while <see cref="Fail"/> is set.</summary>
public sealed class RecordingLivePublisher : ILiveOperationsPublisher
{
    public ConcurrentQueue<LiveOperationMessage> Published { get; } = new();

    public volatile Exception? Fail;

    public int Calls;

    public Task PublishAsync(LiveOperationMessage message, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        if (Fail is { } failure)
        {
            throw failure;
        }

        Published.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A TCP forwarder in front of the broker: <see cref="Down"/> drops every connection and refuses new ones, <see cref="Up"/>
/// accepts them again. Simulates a RabbitMQ outage without restarting the container (which would change its port).
/// </summary>
public sealed class TcpProxy : IAsyncDisposable
{
    private readonly IPEndPoint _target;
    private readonly ConcurrentBag<TcpClient> _connections = [];
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;

    public TcpProxy(Uri amqpUri)
    {
        _target = new IPEndPoint(Dns.GetHostAddresses(amqpUri.Host).First(a => a.AddressFamily == AddressFamily.InterNetwork), amqpUri.Port);
        Port = LiveTestKit.FreePort();
        ConnectionString = new UriBuilder(amqpUri) { Host = "127.0.0.1", Port = Port }.Uri.ToString();
    }

    public int Port { get; }

    public string ConnectionString { get; }

    public void Up()
    {
        _stop = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        _ = AcceptAsync(_listener, _stop.Token);
    }

    public void Down()
    {
        _stop?.Cancel();
        _listener?.Stop();
        while (_connections.TryTake(out var connection))
        {
            connection.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Down();
        return ValueTask.CompletedTask;
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient inbound;
            try
            {
                inbound = await listener.AcceptTcpClientAsync(stop);
            }
            catch (Exception)
            {
                return;
            }

            var outbound = new TcpClient();
            try
            {
                await outbound.ConnectAsync(_target, stop);
            }
            catch (Exception)
            {
                inbound.Dispose();
                outbound.Dispose();
                continue;
            }

            _connections.Add(inbound);
            _connections.Add(outbound);
            _ = PipeAsync(inbound, outbound, stop);
            _ = PipeAsync(outbound, inbound, stop);
        }
    }

    private static async Task PipeAsync(TcpClient from, TcpClient to, CancellationToken stop)
    {
        try
        {
            await from.GetStream().CopyToAsync(to.GetStream(), stop);
        }
        catch (Exception)
        {
            // A dropped connection ends the pipe.
        }
        finally
        {
            from.Dispose();
            to.Dispose();
        }
    }
}
