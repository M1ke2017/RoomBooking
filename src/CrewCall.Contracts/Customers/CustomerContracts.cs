namespace CrewCall.Contracts.Customers;

public sealed record CreateCustomerRequest(string? Name, string? ExternalReference);

public sealed record CustomerResponse(Guid Id, string Name, string? ExternalReference);
