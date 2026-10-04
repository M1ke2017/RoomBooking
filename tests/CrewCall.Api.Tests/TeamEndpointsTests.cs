using System.Net;
using System.Net.Http.Json;
using CrewCall.Contracts.Teams;
using CrewCall.Contracts.Technicians;
using Xunit;

namespace CrewCall.Api.Tests;

public sealed class TeamEndpointsTests(CrewCallApiFactory factory)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Post_team_returns_201_and_get_returns_it()
    {
        using var client = factory.CreateClient();
        var name = $"Team {Guid.NewGuid():N}";

        using var response = await client.PostAsJsonAsync("/api/teams", new CreateTeamRequest(name, null), Cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains(await client.GetFromJsonAsync<TeamResponse[]>("/api/teams", Cancellation) ?? [], team => team.Name == name);
    }

    [Fact]
    public async Task Adding_a_technician_to_a_team_succeeds_and_is_listed()
    {
        using var client = factory.CreateClient();
        var teamId = await ApiTestData.CreateTeamAsync(client);
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);

        using var response = await client.PostAsync($"/api/teams/{teamId}/technicians/{technicianId}", null, Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var members = await client.GetFromJsonAsync<TeamTechnicianResponse[]>($"/api/teams/{teamId}/technicians", Cancellation);
        Assert.Equal(technicianId, Assert.Single(members!).TechnicianId);
        var technicians = await client.GetFromJsonAsync<TechnicianResponse[]>("/api/technicians", Cancellation);
        Assert.Equal(teamId, technicians!.Single(technician => technician.Id == technicianId).TeamId);
    }

    [Fact]
    public async Task Adding_a_technician_to_a_second_team_returns_409()
    {
        using var client = factory.CreateClient();
        var firstTeamId = await ApiTestData.CreateTeamAsync(client);
        var secondTeamId = await ApiTestData.CreateTeamAsync(client);
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);
        (await client.PostAsync($"/api/teams/{firstTeamId}/technicians/{technicianId}", null, Cancellation)).EnsureSuccessStatusCode();

        using var response = await client.PostAsync($"/api/teams/{secondTeamId}/technicians/{technicianId}", null, Cancellation);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Adding_a_technician_to_a_missing_team_returns_404()
    {
        using var client = factory.CreateClient();
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);

        using var response = await client.PostAsync($"/api/teams/{Guid.NewGuid()}/technicians/{technicianId}", null, Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Removing_a_technician_from_a_team_returns_204()
    {
        using var client = factory.CreateClient();
        var teamId = await ApiTestData.CreateTeamAsync(client);
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);
        (await client.PostAsync($"/api/teams/{teamId}/technicians/{technicianId}", null, Cancellation)).EnsureSuccessStatusCode();

        using var response = await client.DeleteAsync($"/api/teams/{teamId}/technicians/{technicianId}", Cancellation);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await client.GetFromJsonAsync<TeamTechnicianResponse[]>($"/api/teams/{teamId}/technicians", Cancellation) ?? []);
    }
}
