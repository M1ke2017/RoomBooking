using CrewCall.Contracts.Customers;
using CrewCall.WorkOrders.Customers;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

internal static class CustomerEndpoints
{
    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var customers = app.MapGroup("/api/customers").WithTags("Customers");

        customers.MapPost("/", CreateAsync).WithName("CreateCustomer");
        customers.MapGet("/", ListAsync).WithName("ListCustomers");

        return app;
    }

    private static async Task<Results<Created<CustomerResponse>, ValidationProblem>> CreateAsync(
        CreateCustomerRequest request, CustomerService customers, CancellationToken cancellationToken)
    {
        var outcome = await customers.CreateAsync(new CreateCustomer(request.Name, request.ExternalReference), cancellationToken);

        return outcome switch
        {
            CreateCustomerOutcome.Created created => TypedResults.Created((string?)null, created.Customer.ToResponse()),
            CreateCustomerOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static async Task<Ok<CustomerResponse[]>> ListAsync(CustomerService customers, CancellationToken cancellationToken)
    {
        var list = await customers.ListAsync(cancellationToken);
        return TypedResults.Ok(list.Select(customer => customer.ToResponse()).ToArray());
    }

    internal static CustomerResponse ToResponse(this Customer customer) =>
        new(customer.Id, customer.Name, customer.ExternalReference);
}
