using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;

namespace CrewCall.Messaging;

/// <summary>
/// The service's broker connection, opened on first use and reopened after it was lost. The address comes from the
/// "crewcall-rabbitmq" connection string, which Aspire supplies (ADR-0014); nothing is hard-coded.
/// <paramref name="clientName"/> names the connection in the RabbitMQ management UI (e.g. "crewcall-reporting").
/// </summary>
public sealed class RabbitMqConnection(IConfiguration configuration, string clientName = "crewcall") : IAsyncDisposable
{
    public const string ConnectionStringName = "crewcall-rabbitmq";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public async Task<IConnection> GetAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } open)
        {
            return open;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true } opened)
            {
                return opened;
            }

            if (_connection is { } lost)
            {
                _connection = null;
                await DisposeQuietlyAsync(lost);
            }

            var connectionString = configuration.GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{ConnectionStringName}' is not configured. Run CrewCall through CrewCall.AppHost.");

            var factory = new ConnectionFactory
            {
                Uri = new Uri(connectionString),
                ClientProvidedName = clientName,
                // Reconnection is done here, on the next use, so a lost connection never hides behind a recovering one.
                AutomaticRecoveryEnabled = false
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is { } connection)
        {
            await DisposeQuietlyAsync(connection);
        }

        _gate.Dispose();
    }

    private static async Task DisposeQuietlyAsync(IConnection connection)
    {
        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception)
        {
            // Already broken; nothing to release.
        }
    }
}
