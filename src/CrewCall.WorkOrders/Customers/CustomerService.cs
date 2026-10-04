using Microsoft.EntityFrameworkCore;

namespace CrewCall.WorkOrders.Customers;

public sealed class CustomerService(IWorkOrdersDbContext db)
{
    public async Task<CreateCustomerOutcome> CreateAsync(CreateCustomer command, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var name = errors.Required("name", command.Name, Customer.NameMaxLength);
        var externalReference = errors.Optional("externalReference", command.ExternalReference, Customer.ExternalReferenceMaxLength);

        if (errors.Any)
        {
            return new CreateCustomerOutcome.Invalid(errors.ToDictionary());
        }

        var customer = new Customer(Guid.CreateVersion7(), name!, externalReference);
        db.Customers.Add(customer);
        await db.SaveChangesAsync(cancellationToken);

        return new CreateCustomerOutcome.Created(customer);
    }

    public async Task<IReadOnlyList<Customer>> ListAsync(CancellationToken cancellationToken) =>
        await db.Customers
            .AsNoTracking()
            .OrderBy(customer => customer.Name)
            .ThenBy(customer => customer.Id)
            .ToListAsync(cancellationToken);
}
