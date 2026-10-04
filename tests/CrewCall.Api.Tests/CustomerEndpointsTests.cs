using System.Net;
using System.Net.Http.Json;
using CrewCall.Contracts.Customers;
using Xunit;

namespace CrewCall.Api.Tests;

public sealed class CustomerEndpointsTests(CrewCallApiFactory factory)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Post_customer_returns_201_with_the_created_customer()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/customers", new CreateCustomerRequest("Acme Facilities", "ERP-1"), Cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var customer = await response.Content.ReadFromJsonAsync<CustomerResponse>(Cancellation);
        Assert.NotNull(customer);
        Assert.NotEqual(Guid.Empty, customer.Id);
        Assert.Equal("Acme Facilities", customer.Name);
        Assert.Equal("ERP-1", customer.ExternalReference);
    }

    [Fact]
    public async Task Post_customer_with_an_empty_name_returns_400()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/customers", new CreateCustomerRequest("  ", null), Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_customers_returns_a_saved_customer()
    {
        using var client = factory.CreateClient();
        var name = $"Customer {Guid.NewGuid():N}";
        (await client.PostAsJsonAsync("/api/customers", new CreateCustomerRequest(name, null), Cancellation)).EnsureSuccessStatusCode();

        var customers = await client.GetFromJsonAsync<CustomerResponse[]>("/api/customers", Cancellation);

        Assert.NotNull(customers);
        Assert.Contains(customers, customer => customer.Name == name);
    }
}
