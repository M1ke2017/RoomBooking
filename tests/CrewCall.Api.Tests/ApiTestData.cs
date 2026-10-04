using System.Net.Http.Json;
using CrewCall.Contracts.Customers;
using CrewCall.Contracts.Sites;
using CrewCall.Contracts.Skills;
using CrewCall.Contracts.Teams;
using CrewCall.Contracts.Technicians;
using CrewCall.Contracts.WorkOrders;
using Xunit;

namespace CrewCall.Api.Tests;

/// <summary>Creates the prerequisite records a test needs through the public API, with unique values.</summary>
internal static class ApiTestData
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static int _holidayDateSequence;

    public static async Task<Guid> CreateTechnicianAsync(
        HttpClient client, string timeZoneId = "Europe/Warsaw", string countryCode = "PL", bool? isActive = null) =>
        (await PostAsync<TechnicianResponse>(client, "/api/technicians",
            new CreateTechnicianRequest("Technician", $"tech-{Guid.NewGuid():N}@crewcall.test", isActive, timeZoneId, countryCode))).Id;

    /// <summary>A far-future date no other test uses: holidays apply to every technician of a country.</summary>
    public static DateOnly UniqueHolidayDate() => new DateOnly(2200, 1, 1).AddDays(Interlocked.Increment(ref _holidayDateSequence));

    public static async Task<Guid> CreateSkillAsync(HttpClient client) =>
        (await PostAsync<SkillResponse>(client, "/api/skills",
            new CreateSkillRequest("Electrical", $"SK-{Guid.NewGuid():N}"[..20], null))).Id;

    public static async Task<Guid> CreateTeamAsync(HttpClient client) =>
        (await PostAsync<TeamResponse>(client, "/api/teams", new CreateTeamRequest($"Team {Guid.NewGuid():N}", null))).Id;

    public static async Task<Guid> CreateCustomerAsync(HttpClient client) =>
        (await PostAsync<CustomerResponse>(client, "/api/customers", new CreateCustomerRequest($"Customer {Guid.NewGuid():N}", null))).Id;

    public static async Task<Guid> CreateSiteAsync(HttpClient client, Guid customerId) =>
        (await PostAsync<SiteResponse>(client, "/api/sites",
            new CreateSiteRequest(customerId, "Plant 1", null, "Poznań", null, "PL", null, null))).Id;

    public static async Task<WorkOrderResponse> CreateWorkOrderAsync(HttpClient client)
    {
        var customerId = await CreateCustomerAsync(client);
        var siteId = await CreateSiteAsync(client, customerId);
        return await PostAsync<WorkOrderResponse>(client, "/api/work-orders",
            new CreateWorkOrderRequest(customerId, siteId, "Replace inverter", null, "High"));
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object request)
    {
        using var response = await client.PostAsJsonAsync(url, request, Cancellation);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Cancellation))!;
    }
}
