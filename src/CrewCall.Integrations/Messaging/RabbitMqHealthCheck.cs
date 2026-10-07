using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CrewCall.Integrations.Messaging;

/// <summary>Readiness of the broker connection for this service. The API does not depend on it (ADR-0014).</summary>
public sealed class RabbitMqHealthCheck(RabbitMqConnection connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var open = await connection.GetAsync(timeout.Token);
            return open.IsOpen ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("The RabbitMQ connection is closed.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ is unreachable.", exception);
        }
    }
}
