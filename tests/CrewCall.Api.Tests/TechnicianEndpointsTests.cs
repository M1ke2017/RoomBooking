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

        using var response = await client.PostAsJsonAsync("/api/technicians", new CreateTechnicianRequest("Anna Kowalska", email, null, "Europe/Warsaw", "PL"), Cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var technician = await response.Content.ReadFromJsonAsync<TechnicianResponse>(Cancellation);
        Assert.NotNull(technician);
        Assert.Equal(email, technician.Email);
        Assert.True(technician.IsActive);
        Assert.Equal("Europe/Warsaw", technician.TimeZoneId);
        Assert.Equal("PL", technician.CountryCode);
    }

    [Theory]
    [InlineData(null, "PL")]
    [InlineData("Central European Standard Time", "PL")]
    [InlineData("Europe/Warsaw", null)]
    [InlineData("Europe/Warsaw", "POL")]
    public async Task Post_technician_without_a_valid_time_zone_or_country_returns_400(string? timeZoneId, string? countryCode)
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/technicians", new CreateTechnicianRequest("Anna Kowalska", UniqueEmail(), null, timeZoneId, countryCode), Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("+48601234567", "+48601234567")]
    [InlineData(" +48 601-234-567 ", "+48601234567")]
    [InlineData("+1 (415) 555.0100", "+14155550100")]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    public async Task Post_technician_stores_an_optional_phone_number_in_E164_form(string? phoneNumber, string? stored)
    {
        using var client = factory.CreateClient();
        var email = UniqueEmail();

        using var response = await client.PostAsJsonAsync(
            "/api/technicians", new CreateTechnicianRequest("Anna Kowalska", email, null, "Europe/Warsaw", "PL", phoneNumber), Cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(stored, (await response.Content.ReadFromJsonAsync<TechnicianResponse>(Cancellation))!.PhoneNumber);
        var listed = await client.GetFromJsonAsync<TechnicianResponse[]>("/api/technicians", Cancellation);
        Assert.Equal(stored, Assert.Single(listed!, technician => technician.Email == email).PhoneNumber);
    }

    [Theory]
    [InlineData("601234567")] // no country code
    [InlineData("+0601234567")] // country codes do not start with 0
    [InlineData("+48 601 234 567 890 12")] // more than 15 digits
    [InlineData("+48123")] // too short
    [InlineData("+48 601 ABC 567")]
    public async Task Post_technician_with_a_phone_number_that_is_not_E164_returns_400(string phoneNumber)
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/technicians", new CreateTechnicianRequest("Anna Kowalska", UniqueEmail(), null, "Europe/Warsaw", "PL", phoneNumber), Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>(Cancellation);
        Assert.Equal(["phoneNumber"], problem!.Errors.Keys);
    }

    [Fact]
    public async Task Post_technician_with_a_duplicate_email_returns_409()
    {
        using var client = factory.CreateClient();
        var email = UniqueEmail();
        (await client.PostAsJsonAsync("/api/technicians", new CreateTechnicianRequest("First", email, null, "Europe/Warsaw", "PL"), Cancellation)).EnsureSuccessStatusCode();

        using var response = await client.PostAsJsonAsync("/api/technicians", new CreateTechnicianRequest("Second", email, null, "Europe/Warsaw", "PL"), Cancellation);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Post_technician_with_an_invalid_email_returns_400()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/technicians", new CreateTechnicianRequest("Anna Kowalska", "not-an-email", null, "Europe/Warsaw", "PL"), Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_technicians_returns_a_saved_technician()
    {
        using var client = factory.CreateClient();
        var email = UniqueEmail();
        (await client.PostAsJsonAsync("/api/technicians", new CreateTechnicianRequest("Jan Nowak", email, false, "Europe/Warsaw", "PL"), Cancellation)).EnsureSuccessStatusCode();

        var technicians = await client.GetFromJsonAsync<TechnicianResponse[]>("/api/technicians", Cancellation);

        Assert.NotNull(technicians);
        var saved = Assert.Single(technicians, technician => technician.Email == email);
        Assert.False(saved.IsActive);
    }
}
