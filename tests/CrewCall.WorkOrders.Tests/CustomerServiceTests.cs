using CrewCall.WorkOrders.Customers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.WorkOrders.Tests;

public sealed class CustomerServiceTests(WorkOrdersDatabase database)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_saves_a_valid_customer_with_a_trimmed_name()
    {
        await using var scope = database.CreateScope();
        var customers = scope.ServiceProvider.GetRequiredService<CustomerService>();

        var outcome = await customers.CreateAsync(new CreateCustomer("  Acme Facilities  ", "ERP-1001"), Cancellation);

        var created = Assert.IsType<CreateCustomerOutcome.Created>(outcome);
        Assert.NotEqual(Guid.Empty, created.Customer.Id);
        Assert.Equal("Acme Facilities", created.Customer.Name);
        Assert.Equal("ERP-1001", created.Customer.ExternalReference);
        Assert.Contains(await customers.ListAsync(Cancellation), customer => customer.Id == created.Customer.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_rejects_an_empty_name(string? name)
    {
        await using var scope = database.CreateScope();
        var customers = scope.ServiceProvider.GetRequiredService<CustomerService>();

        var outcome = await customers.CreateAsync(new CreateCustomer(name, null), Cancellation);

        var invalid = Assert.IsType<CreateCustomerOutcome.Invalid>(outcome);
        Assert.Contains("name", invalid.Errors.Keys);
    }
}
