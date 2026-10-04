using System.Net.Http.Json;
using CrewCall.Contracts.Skills;
using CrewCall.Contracts.Teams;
using CrewCall.Contracts.Technicians;
using Xunit;

namespace CrewCall.Api.Tests;

/// <summary>Creates the prerequisite records a test needs through the public API, with unique values.</summary>
internal static class ApiTestData
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public static async Task<Guid> CreateTechnicianAsync(HttpClient client) =>
        (await PostAsync<TechnicianResponse>(client, "/api/technicians",
            new CreateTechnicianRequest("Technician", $"tech-{Guid.NewGuid():N}@crewcall.test", null))).Id;

    public static async Task<Guid> CreateSkillAsync(HttpClient client) =>
        (await PostAsync<SkillResponse>(client, "/api/skills",
            new CreateSkillRequest("Electrical", $"SK-{Guid.NewGuid():N}"[..20], null))).Id;

    public static async Task<Guid> CreateTeamAsync(HttpClient client) =>
        (await PostAsync<TeamResponse>(client, "/api/teams", new CreateTeamRequest($"Team {Guid.NewGuid():N}", null))).Id;

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object request)
    {
        using var response = await client.PostAsJsonAsync(url, request, Cancellation);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Cancellation))!;
    }
}
