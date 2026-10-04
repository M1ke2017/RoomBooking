using CrewCall.Contracts.Sites;
using CrewCall.WorkOrders.Sites;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

internal static class SiteEndpoints
{
    public static IEndpointRouteBuilder MapSiteEndpoints(this IEndpointRouteBuilder app)
    {
        var sites = app.MapGroup("/api/sites").WithTags("Sites");
        sites.MapPost("/", CreateAsync).WithName("CreateSite");
        sites.MapGet("/", ListAsync).WithName("ListSites");

        app.MapGet("/api/customers/{customerId:guid}/sites", ListForCustomerAsync)
            .WithTags("Sites")
            .WithName("ListCustomerSites");

        return app;
    }

    private static async Task<Results<Created<SiteResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateSiteRequest request, SiteService sites, CancellationToken cancellationToken)
    {
        var command = new CreateSite(
            request.CustomerId,
            request.Name,
            request.AddressLine1,
            request.City,
            request.PostalCode,
            request.CountryCode,
            request.Latitude,
            request.Longitude);

        var outcome = await sites.CreateAsync(command, cancellationToken);

        return outcome switch
        {
            CreateSiteOutcome.Created created => TypedResults.Created((string?)null, created.Site.ToResponse()),
            CreateSiteOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            CreateSiteOutcome.CustomerNotFound notFound => CustomerNotFound(notFound.CustomerId),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static async Task<Ok<SiteResponse[]>> ListAsync(SiteService sites, CancellationToken cancellationToken)
    {
        var list = await sites.ListAsync(cancellationToken);
        return TypedResults.Ok(list.Select(site => site.ToResponse()).ToArray());
    }

    private static async Task<Results<Ok<SiteResponse[]>, ProblemHttpResult>> ListForCustomerAsync(
        Guid customerId, SiteService sites, CancellationToken cancellationToken)
    {
        var list = await sites.ListForCustomerAsync(customerId, cancellationToken);

        return list is null
            ? CustomerNotFound(customerId)
            : TypedResults.Ok(list.Select(site => site.ToResponse()).ToArray());
    }

    private static ProblemHttpResult CustomerNotFound(Guid customerId) =>
        ApiProblems.NotFound("Customer not found", $"Customer '{customerId}' does not exist.");

    internal static SiteResponse ToResponse(this Site site) =>
        new(
            site.Id,
            site.CustomerId,
            site.Name,
            site.AddressLine1,
            site.City,
            site.PostalCode,
            site.CountryCode,
            site.Latitude,
            site.Longitude);
}
