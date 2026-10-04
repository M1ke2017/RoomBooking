namespace CrewCall.Contracts.Sites;

public sealed record CreateSiteRequest(
    Guid CustomerId,
    string? Name,
    string? AddressLine1,
    string? City,
    string? PostalCode,
    string? CountryCode,
    double? Latitude,
    double? Longitude);

public sealed record SiteResponse(
    Guid Id,
    Guid CustomerId,
    string Name,
    string? AddressLine1,
    string City,
    string? PostalCode,
    string CountryCode,
    double? Latitude,
    double? Longitude);
