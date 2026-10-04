namespace CrewCall.WorkOrders.Sites;

/// <summary>
/// A physical location of a customer where field work is carried out. Not a bookable resource:
/// a site is where a technician travels to, not something that is reserved.
/// </summary>
public sealed class Site
{
    public const int NameMaxLength = 200;
    public const int AddressLine1MaxLength = 200;
    public const int CityMaxLength = 100;
    public const int PostalCodeMaxLength = 20;
    public const int CountryCodeLength = 2;

    internal Site(
        Guid id,
        Guid customerId,
        string name,
        string? addressLine1,
        string city,
        string? postalCode,
        string countryCode,
        double? latitude,
        double? longitude)
    {
        Id = id;
        CustomerId = customerId;
        Name = name;
        AddressLine1 = addressLine1;
        City = city;
        PostalCode = postalCode;
        CountryCode = countryCode;
        Latitude = latitude;
        Longitude = longitude;
    }

    public Guid Id { get; private set; }

    public Guid CustomerId { get; private set; }

    public string Name { get; private set; }

    public string? AddressLine1 { get; private set; }

    public string City { get; private set; }

    public string? PostalCode { get; private set; }

    /// <summary>ISO 3166-1 alpha-2 country code, upper case (e.g. "PL").</summary>
    public string CountryCode { get; private set; }

    public double? Latitude { get; private set; }

    public double? Longitude { get; private set; }
}
