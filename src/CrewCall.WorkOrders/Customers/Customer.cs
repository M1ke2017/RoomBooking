namespace CrewCall.WorkOrders.Customers;

/// <summary>A company or organisation CrewCall works for. Owns one or more <see cref="Sites.Site"/>s.</summary>
public sealed class Customer
{
    public const int NameMaxLength = 200;
    public const int ExternalReferenceMaxLength = 100;

    internal Customer(Guid id, string name, string? externalReference)
    {
        Id = id;
        Name = name;
        ExternalReference = externalReference;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    /// <summary>The customer's identifier in an external system (e.g. ERP or CRM), if any.</summary>
    public string? ExternalReference { get; private set; }
}
