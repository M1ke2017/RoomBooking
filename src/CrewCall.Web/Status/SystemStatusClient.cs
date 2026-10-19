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
        var apiService = httpClient.BaseAddress?.ToString() ?? "(nie skonfigurowano)";

        var api = await ProbeApiAsync(cancellationToken);
        var database = api.State == ComponentState.Healthy
            ? await ProbeDatabaseAsync(cancellationToken)
            : new ComponentStatus(ComponentState.Unknown, "Brak danych: API jest nieosiągalne.");

        return new SystemStatus(api, database, apiService, DateTimeOffset.UtcNow);
    }

    private async Task<ComponentStatus> ProbeApiAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync("/alive", cancellationToken);

            return response.IsSuccessStatusCode
                ? new ComponentStatus(ComponentState.Healthy, "Test działania zaliczony.")
                : new ComponentStatus(ComponentState.Unavailable, $"Test działania zwrócił HTTP {(int)response.StatusCode}.");
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
                "Healthy" => new ComponentStatus(ComponentState.Healthy, "Test bazy danych zaliczony."),
                "Degraded" => new ComponentStatus(ComponentState.Degraded, check.Description ?? "Baza danych działa częściowo."),
                "Unhealthy" => new ComponentStatus(ComponentState.Unavailable, check.Description ?? "Test bazy danych nieudany."),
                _ => new ComponentStatus(ComponentState.Unknown, "API nie zgłosiło stanu bazy danych.")
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ComponentStatus(ComponentState.Unavailable,
                $"Brak odpowiedzi testu bazy danych w ciągu {httpClient.Timeout.TotalSeconds:0} s.");
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
