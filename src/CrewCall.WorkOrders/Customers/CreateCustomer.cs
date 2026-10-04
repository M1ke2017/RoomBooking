namespace CrewCall.WorkOrders.Customers;

public sealed record CreateCustomer(string? Name, string? ExternalReference);

public abstract record CreateCustomerOutcome
{
    private CreateCustomerOutcome()
    {
    }

    public sealed record Created(Customer Customer) : CreateCustomerOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CreateCustomerOutcome;
}
