using Microsoft.EntityFrameworkCore;

namespace CrewCall.WorkOrders.Sites;

public sealed class SiteService(IWorkOrdersDbContext db)
{
    public async Task<CreateSiteOutcome> CreateAsync(CreateSite command, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();

        if (command.CustomerId == Guid.Empty)
        {
            errors.Add("customerId", "Required.");
        }

        var name = errors.Required("name", command.Name, Site.NameMaxLength);
        var addressLine1 = errors.Optional("addressLine1", command.AddressLine1, Site.AddressLine1MaxLength);
        var city = errors.Required("city", command.City, Site.CityMaxLength);
        var postalCode = errors.Optional("postalCode", command.PostalCode, Site.PostalCodeMaxLength);
        var countryCode = ValidateCountryCode(errors, command.CountryCode);
        ValidateCoordinates(errors, command.Latitude, command.Longitude);

        if (errors.Any)
        {
            return new CreateSiteOutcome.Invalid(errors.ToDictionary());
        }

        if (!await db.Customers.AnyAsync(customer => customer.Id == command.CustomerId, cancellationToken))
        {
            return new CreateSiteOutcome.CustomerNotFound(command.CustomerId);
        }

        var site = new Site(
            Guid.CreateVersion7(),
            command.CustomerId,
            name!,
            addressLine1,
            city!,
            postalCode,
            countryCode!,
            command.Latitude,
            command.Longitude);

        db.Sites.Add(site);
        await db.SaveChangesAsync(cancellationToken);

        return new CreateSiteOutcome.Created(site);
    }

    public async Task<IReadOnlyList<Site>> ListAsync(CancellationToken cancellationToken) =>
        await db.Sites
            .AsNoTracking()
            .OrderBy(site => site.Name)
            .ThenBy(site => site.Id)
            .ToListAsync(cancellationToken);

    /// <summary>Returns the customer's sites, or null when the customer does not exist.</summary>
    public async Task<IReadOnlyList<Site>?> ListForCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        if (!await db.Customers.AnyAsync(customer => customer.Id == customerId, cancellationToken))
        {
            return null;
        }

        return await db.Sites
            .AsNoTracking()
            .Where(site => site.CustomerId == customerId)
            .OrderBy(site => site.Name)
            .ThenBy(site => site.Id)
            .ToListAsync(cancellationToken);
    }

    private static string? ValidateCountryCode(ValidationErrors errors, string? value)
    {
        var countryCode = errors.Required("countryCode", value, Site.CountryCodeLength);
        if (countryCode is null || countryCode.Length > Site.CountryCodeLength)
        {
            return countryCode;
        }

        if (countryCode.Length != Site.CountryCodeLength || !countryCode.All(char.IsAsciiLetter))
        {
            errors.Add("countryCode", "Must be a two-letter ISO 3166-1 alpha-2 code.");
            return countryCode;
        }

        return countryCode.ToUpperInvariant();
    }

    private static void ValidateCoordinates(ValidationErrors errors, double? latitude, double? longitude)
    {
        if (latitude.HasValue != longitude.HasValue)
        {
            errors.Add(latitude.HasValue ? "longitude" : "latitude", "Latitude and longitude must be provided together.");
            return;
        }

        if (latitude is < -90 or > 90)
        {
            errors.Add("latitude", "Must be between -90 and 90.");
        }

        if (longitude is < -180 or > 180)
        {
            errors.Add("longitude", "Must be between -180 and 180.");
        }
    }
}
