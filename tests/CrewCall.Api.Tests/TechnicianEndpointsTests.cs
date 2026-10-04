using System.Net;
using System.Net.Http.Json;
using CrewCall.Contracts.Technicians;
using Xunit;

namespace CrewCall.Api.Tests;

public sealed class TechnicianEndpointsTests(CrewCallApiFactory factory)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static string UniqueEmail() => $"tech-{Guid.NewGuid():N}@crewcall.test";

    [Fact]
    public async Task Post_technician_returns_201_with_an_active_technician()
    {
        using var client = factory.CreateClient();
        var email = UniqueEmail();

        using var response = await client.PostAsJsonAsync("/api/technicians", new CreateTechnicianRequest("Anna Kowalska", email, null), Cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var technician = await response.Content.ReadFromJsonAsync<TechnicianResponse>(Cancellation);
        Assert.NotNull(technician);
        Assert.Equal(email, technician.Email);
        Assert.True(technician.IsActive);
    }

    [Fact]
    public async Task Post_technician_with_a_duplicate_email_returns_409()
    {
        using var client = factory.CreateClient();
        var email = UniqueEmail();
        (await client.PostAsJsonAsync("/api/technicians", new CreateTechnicianRequest("First", email, null), Cancellation)).EnsureSuccessStatusCode();

        using var response = await client.PostAsJsonAsync("/api/technicians", new CreateTechnicianRequest("Second", email, null), Cancellation);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Post_technician_with_an_invalid_email_returns_400()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/technicians", new CreateTechnicianRequest("Anna Kowalska", "not-an-email", null), Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_technicians_returns_a_saved_technician()
    {
        using var client = factory.CreateClient();
        var email = UniqueEmail();
        (await client.PostAsJsonAsync("/api/technicians", new CreateTechnicianRequest("Jan Nowak", email, false), Cancellation)).EnsureSuccessStatusCode();

        var technicians = await client.GetFromJsonAsync<TechnicianResponse[]>("/api/technicians", Cancellation);

        Assert.NotNull(technicians);
        var saved = Assert.Single(technicians, technician => technician.Email == email);
        Assert.False(saved.IsActive);
    }
}
