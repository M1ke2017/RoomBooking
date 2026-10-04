using CrewCall.WorkOrders.Customers;
using CrewCall.WorkOrders.Sites;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.WorkOrders.Tests;

public sealed class SiteServiceTests(WorkOrdersDatabase database)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_saves_a_site_for_an_existing_customer()
    {
        await using var scope = database.CreateScope();
        var customerId = await CreateCustomerAsync(scope);
        var sites = scope.ServiceProvider.GetRequiredService<SiteService>();

        var outcome = await sites.CreateAsync(
            new CreateSite(customerId, "Warehouse North", "ul. Polna 1", "Poznań", "60-001", "pl", 52.4064, 16.9252),
            Cancellation);

        var created = Assert.IsType<CreateSiteOutcome.Created>(outcome);
        Assert.Equal(customerId, created.Site.CustomerId);
        Assert.Equal("PL", created.Site.CountryCode);
        var customerSites = await sites.ListForCustomerAsync(customerId, Cancellation);
        Assert.NotNull(customerSites);
        Assert.Equal(created.Site.Id, Assert.Single(customerSites).Id);
    }

    [Fact]
    public async Task Create_rejects_a_site_for_a_missing_customer()
    {
        await using var scope = database.CreateScope();
        var sites = scope.ServiceProvider.GetRequiredService<SiteService>();
        var missingCustomerId = Guid.NewGuid();

        var outcome = await sites.CreateAsync(
            new CreateSite(missingCustomerId, "Warehouse North", null, "Poznań", null, "PL", null, null),
            Cancellation);

        var notFound = Assert.IsType<CreateSiteOutcome.CustomerNotFound>(outcome);
        Assert.Equal(missingCustomerId, notFound.CustomerId);
    }

    [Fact]
    public async Task Create_rejects_an_empty_site_name()
    {
        await using var scope = database.CreateScope();
        var customerId = await CreateCustomerAsync(scope);
        var sites = scope.ServiceProvider.GetRequiredService<SiteService>();

        var outcome = await sites.CreateAsync(
            new CreateSite(customerId, "  ", null, "Poznań", null, "PL", null, null),
            Cancellation);

        var invalid = Assert.IsType<CreateSiteOutcome.Invalid>(outcome);
        Assert.Contains("name", invalid.Errors.Keys);
    }

    [Fact]
    public async Task Create_rejects_a_missing_city_and_an_invalid_country_code()
    {
        await using var scope = database.CreateScope();
        var customerId = await CreateCustomerAsync(scope);
        var sites = scope.ServiceProvider.GetRequiredService<SiteService>();

        var outcome = await sites.CreateAsync(
            new CreateSite(customerId, "Warehouse North", null, null, null, "P1", null, null),
            Cancellation);

        var invalid = Assert.IsType<CreateSiteOutcome.Invalid>(outcome);
        Assert.Contains("city", invalid.Errors.Keys);
        Assert.Contains("countryCode", invalid.Errors.Keys);
    }

    private static async Task<Guid> CreateCustomerAsync(AsyncServiceScope scope)
    {
        var outcome = await scope.ServiceProvider.GetRequiredService<CustomerService>()
            .CreateAsync(new CreateCustomer($"Customer {Guid.NewGuid():N}", null), Cancellation);

        return Assert.IsType<CreateCustomerOutcome.Created>(outcome).Customer.Id;
    }
}
