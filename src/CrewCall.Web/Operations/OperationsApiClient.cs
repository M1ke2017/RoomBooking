using System.Net.Http.Json;
using CrewCall.Contracts.Incidents;
using CrewCall.Contracts.Operations;
using CrewCall.Contracts.Reporting;
using Microsoft.AspNetCore.Mvc;

namespace CrewCall.Web.Operations;

/// <summary>A call's result: the value, or what the API said went wrong (its problem title and detail).</summary>
public sealed record ApiResult<T>(T? Value, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// The manager screen's calls to CrewCall.Api (found through Aspire service discovery). Contracts in, contracts out:
/// the screen shows what the API returns, it never rebuilds business state on its own.
/// </summary>
public sealed class OperationsApiClient(HttpClient http)
{
    public Task<ApiResult<OperationsBoardResponse>> GetBoardAsync(DateOnly? date, CancellationToken cancellationToken) =>
        SendAsync<OperationsBoardResponse>(
            () => http.GetAsync(date is { } day ? $"/api/operations/board?date={day:yyyy-MM-dd}" : "/api/operations/board", cancellationToken),
            cancellationToken);

    /// <summary>Analyze against every active technician (the incident's own skills decide who fits).</summary>
    public Task<ApiResult<IncidentAnalysisResponse>> AnalyzeAsync(Guid incidentId, CancellationToken cancellationToken) =>
        SendAsync<IncidentAnalysisResponse>(
            () => http.PostAsJsonAsync($"/api/incidents/{incidentId}/analyze", new AnalyzeIncidentRequest(null, null), cancellationToken),
            cancellationToken);

    public Task<ApiResult<RescheduleProposal[]>> ProposeAsync(Guid incidentId, CancellationToken cancellationToken) =>
        SendAsync<RescheduleProposal[]>(
            () => http.PostAsJsonAsync($"/api/incidents/{incidentId}/reschedule-proposal", new RescheduleProposalRequest(null, null, null, null, null), cancellationToken),
            cancellationToken);

    /// <summary>The manager accepts the proposal exactly as shown; the API checks everything again (409 when stale).</summary>
    public Task<ApiResult<ApplyRescheduleResponse>> AcceptAsync(Guid incidentId, RescheduleProposal proposal, CancellationToken cancellationToken) =>
        SendAsync<ApplyRescheduleResponse>(
            () => http.PostAsJsonAsync(
                $"/api/incidents/{incidentId}/reschedule-proposal/apply",
                new ApplyRescheduleRequest(
                    proposal.RecommendedTechnicianId, proposal.VehicleId, proposal.EquipmentIds,
                    proposal.TravelBufferBeforeMinutes, proposal.TravelBufferAfterMinutes,
                    proposal.ConflictingVisitId, proposal.ProposedNewStartUtc, proposal.ProposedNewEndUtc),
                cancellationToken),
            cancellationToken);

    /// <summary>Today's demo plan (only where the API enables demo data).</summary>
    public Task<ApiResult<object>> SeedDemoAsync(CancellationToken cancellationToken) =>
        SendAsync<object>(() => http.PostAsync("/api/demo/seed", null, cancellationToken), cancellationToken);

    private static async Task<ApiResult<T>> SendAsync<T>(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send();
            if (response.IsSuccessStatusCode)
            {
                return new ApiResult<T>(await response.Content.ReadFromJsonAsync<T>(cancellationToken), null);
            }

            var problem = await ReadProblemAsync(response, cancellationToken);
            return new ApiResult<T>(default, problem ?? $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new ApiResult<T>(default, $"CrewCall.Api jest niedostępne ({exception.Message}).");
        }
    }

    private static async Task<string?> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(cancellationToken);
            var errors = problem?.Errors.SelectMany(error => error.Value).ToArray() ?? [];
            return problem is null ? null : string.Join(" ", new[] { problem.Title, problem.Detail }.Concat(errors).Where(text => !string.IsNullOrWhiteSpace(text)));
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>The day's numbers from CrewCall.Reporting (eventually consistent; null while it is unreachable).</summary>
public sealed class ReportingApiClient(HttpClient http)
{
    public async Task<VisitActivityReport?> GetVisitsAsync(DateOnly day, CancellationToken cancellationToken)
    {
        try
        {
            return await http.GetFromJsonAsync<VisitActivityReport>($"/api/reporting/visits?start={day:yyyy-MM-dd}&end={day:yyyy-MM-dd}", cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
