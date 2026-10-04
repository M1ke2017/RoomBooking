using System.Net;
using System.Net.Http.Json;
using CrewCall.Contracts.Customers;
using CrewCall.Contracts.Sites;
using Xunit;

namespace CrewCall.Api.Tests;

public sealed class SiteEndpointsTests(CrewCallApiFactory factory)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static CreateSiteRequest SiteFor(Guid customerId, string name = "Warehouse North") =>
        new(customerId, name, "ul. Polna 1", "Poznań", "60-001", "PL", 52.4064, 16.9252);

    [Fact]
    public async Task Post_site_for_an_existing_customer_returns_201()
    {
        using var client = factory.CreateClient();
        var customerId = await CreateCustomerAsync(client);

        using var response = await client.PostAsJsonAsync("/api/sites", SiteFor(customerId), Cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var site = await response.Content.ReadFromJsonAsync<SiteResponse>(Cancellation);
        Assert.NotNull(site);
        Assert.Equal(customerId, site.CustomerId);
        Assert.Equal("Poznań", site.City);
        Assert.Equal("PL", site.CountryCode);
    }

    [Fact]
    public async Task Post_site_for_a_missing_customer_returns_404()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/sites", SiteFor(Guid.NewGuid()), Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_sites_and_customer_sites_return_a_saved_site()
    {
        using var client = factory.CreateClient();
        var customerId = await CreateCustomerAsync(client);
        var name = $"Site {Guid.NewGuid():N}";
        (await client.PostAsJsonAsync("/api/sites", SiteFor(customerId, name), Cancellation)).EnsureSuccessStatusCode();

        var allSites = await client.GetFromJsonAsync<SiteResponse[]>("/api/sites", Cancellation);
        var customerSites = await client.GetFromJsonAsync<SiteResponse[]>($"/api/customers/{customerId}/sites", Cancellation);

        Assert.NotNull(allSites);
        Assert.Contains(allSites, site => site.Name == name);
        Assert.NotNull(customerSites);
        Assert.Equal(name, Assert.Single(customerSites).Name);
    }

    [Fact]
    public async Task Get_sites_of_a_missing_customer_returns_404()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/customers/{Guid.NewGuid()}/sites", Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/customers", new CreateCustomerRequest($"Customer {Guid.NewGuid():N}", null), Cancellation);
        response.EnsureSuccessStatusCode();

        var customer = await response.Content.ReadFromJsonAsync<CustomerResponse>(Cancellation);
        return customer!.Id;
    }
}
