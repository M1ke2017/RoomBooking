namespace CrewCall.WorkOrders.Sites;

public sealed record CreateSite(
    Guid CustomerId,
    string? Name,
    string? AddressLine1,
    string? City,
    string? PostalCode,
    string? CountryCode,
    double? Latitude,
    double? Longitude);

public abstract record CreateSiteOutcome
{
    private CreateSiteOutcome()
    {
    }

    public sealed record Created(Site Site) : CreateSiteOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CreateSiteOutcome;

    public sealed record CustomerNotFound(Guid CustomerId) : CreateSiteOutcome;
}
