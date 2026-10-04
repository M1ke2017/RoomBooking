using System.Net;
using System.Net.Http.Json;
using CrewCall.Contracts.Skills;
using Xunit;

namespace CrewCall.Api.Tests;

public sealed class SkillEndpointsTests(CrewCallApiFactory factory)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Post_skill_returns_201_and_get_returns_it()
    {
        using var client = factory.CreateClient();
        var code = $"FIB-{Guid.NewGuid():N}"[..20];

        using var response = await client.PostAsJsonAsync("/api/skills", new CreateSkillRequest("Fiber", code, null), Cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var skill = await response.Content.ReadFromJsonAsync<SkillResponse>(Cancellation);
        Assert.Equal(code.ToUpperInvariant(), skill!.Code);
        Assert.Contains(await client.GetFromJsonAsync<SkillResponse[]>("/api/skills", Cancellation) ?? [], s => s.Id == skill.Id);
    }

    [Fact]
    public async Task Post_skill_with_a_duplicate_code_returns_409()
    {
        using var client = factory.CreateClient();
        var code = $"HVAC-{Guid.NewGuid():N}"[..20];
        (await client.PostAsJsonAsync("/api/skills", new CreateSkillRequest("HVAC", code, null), Cancellation)).EnsureSuccessStatusCode();

        using var response = await client.PostAsJsonAsync("/api/skills", new CreateSkillRequest("HVAC 2", code.ToLowerInvariant(), null), Cancellation);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Post_skill_with_an_empty_name_returns_400()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/skills", new CreateSkillRequest(" ", null, null), Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Assigning_a_skill_returns_201_then_200_when_repeated_and_never_duplicates()
    {
        using var client = factory.CreateClient();
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);
        var skillId = await ApiTestData.CreateSkillAsync(client);
        var url = $"/api/technicians/{technicianId}/skills/{skillId}";

        using var first = await client.PostAsync(url, null, Cancellation);
        using var second = await client.PostAsync(url, null, Cancellation);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var skills = await client.GetFromJsonAsync<TechnicianSkillResponse[]>($"/api/technicians/{technicianId}/skills", Cancellation);
        Assert.Equal(skillId, Assert.Single(skills!).SkillId);
    }

    [Fact]
    public async Task Assigning_a_skill_to_a_missing_technician_returns_404()
    {
        using var client = factory.CreateClient();
        var skillId = await ApiTestData.CreateSkillAsync(client);

        using var response = await client.PostAsync($"/api/technicians/{Guid.NewGuid()}/skills/{skillId}", null, Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Removing_a_skill_returns_204_and_unlinks_it()
    {
        using var client = factory.CreateClient();
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);
        var skillId = await ApiTestData.CreateSkillAsync(client);
        (await client.PostAsync($"/api/technicians/{technicianId}/skills/{skillId}", null, Cancellation)).EnsureSuccessStatusCode();

        using var response = await client.DeleteAsync($"/api/technicians/{technicianId}/skills/{skillId}", Cancellation);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await client.GetFromJsonAsync<TechnicianSkillResponse[]>($"/api/technicians/{technicianId}/skills", Cancellation) ?? []);
    }
}
