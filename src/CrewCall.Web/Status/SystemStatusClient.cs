using System.Text.Json;

namespace CrewCall.Web.Status;

/// <summary>
/// Reads CrewCall.Api's health endpoints: /alive reports the API runtime, /health reports its dependencies (the database).
/// Probing them separately means a slow or failing database is never reported as an unavailable API.
/// </summary>
public sealed class SystemStatusClient(HttpClient httpClient, ILogger<SystemStatusClient> logger)
{
    private const string DatabaseCheck = "database";

    public async Task<SystemStatus> GetAsync(CancellationToken cancellationToken = default)
    {
        var apiService = httpClient.BaseAddress?.ToString() ?? "(not configured)";

        var api = await ProbeApiAsync(cancellationToken);
        var database = api.State == ComponentState.Healthy
            ? await ProbeDatabaseAsync(cancellationToken)
            : new ComponentStatus(ComponentState.Unknown, "Not reported: the API is unreachable.");

        return new SystemStatus(api, database, apiService, DateTimeOffset.UtcNow);
    }

    private async Task<ComponentStatus> ProbeApiAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync("/alive", cancellationToken);

            return response.IsSuccessStatusCode
                ? new ComponentStatus(ComponentState.Healthy, "Liveness check passed.")
                : new ComponentStatus(ComponentState.Unavailable, $"Liveness check returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "CrewCall.Api liveness check failed.");
            return new ComponentStatus(ComponentState.Unavailable, ex.Message);
        }
    }

    private async Task<ComponentStatus> ProbeDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync("/health", cancellationToken);
            var report = await response.Content.ReadFromJsonAsync<HealthReport>(cancellationToken);
            var check = report?.Checks.FirstOrDefault(c => c.Name == DatabaseCheck);

            return check?.Status switch
            {
                "Healthy" => new ComponentStatus(ComponentState.Healthy, "Database check passed."),
                "Degraded" => new ComponentStatus(ComponentState.Degraded, check.Description ?? "Database check degraded."),
                "Unhealthy" => new ComponentStatus(ComponentState.Unavailable, check.Description ?? "Database check failed."),
                _ => new ComponentStatus(ComponentState.Unknown, "Database check not reported by the API.")
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ComponentStatus(ComponentState.Unavailable,
                $"No response from the database health check within {httpClient.Timeout.TotalSeconds:0} s.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "CrewCall.Api health check failed.");
            return new ComponentStatus(ComponentState.Unknown, ex.Message);
        }
    }

    private sealed record HealthReport(string Status, IReadOnlyList<HealthCheckEntry> Checks);

    private sealed record HealthCheckEntry(string Name, string Status, string? Description);
}
