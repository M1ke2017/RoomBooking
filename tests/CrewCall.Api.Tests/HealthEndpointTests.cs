using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace CrewCall.Api.Tests;

public sealed class HealthEndpointTests(CrewCallApiFactory factory) : IClassFixture<CrewCallApiFactory>
{
    [Fact]
    public async Task Health_returns_200_and_a_healthy_database_check_when_the_database_is_reachable()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        var report = await response.Content.ReadFromJsonAsync<HealthReport>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(report);
        Assert.Equal("Healthy", report.Status);
        Assert.Contains(report.Checks, check => check is { Name: "database", Status: "Healthy" });
    }

    [Fact]
    public async Task Alive_returns_200_when_the_api_runtime_is_up()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/alive", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed record HealthReport(string Status, IReadOnlyList<HealthCheckEntry> Checks);

    private sealed record HealthCheckEntry(string Name, string Status);
}
