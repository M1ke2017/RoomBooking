using System.Globalization;
using CrewCall.Contracts.Availability;
using CrewCall.Workforce.Availability;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

internal static class AvailabilityEndpoints
{
    // ISO 8601 with an explicit offset or "Z". A time without an offset is rejected: it would otherwise be read in the
    // server's time zone, which says nothing about the technician.
    private static readonly string[] _offsetFormats =
    [
        "yyyy-MM-dd'T'HH:mmzzz",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
    ];

    private static readonly string[] _utcFormats =
    [
        "yyyy-MM-dd'T'HH:mm'Z'",
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"
    ];

    public static IEndpointRouteBuilder MapAvailabilityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/technicians/{technicianId:guid}/availability", CheckAsync)
            .WithTags("Availability")
            .WithName("GetTechnicianAvailability");

        return app;
    }

    /// <summary>?start=2026-03-30T06:00:00Z&amp;end=2026-03-30T14:00:00Z — is the technician available for all of [start, end)?</summary>
    private static async Task<Results<Ok<TechnicianAvailabilityResponse>, ValidationProblem, ProblemHttpResult>> CheckAsync(
        Guid technicianId, string? start, string? end, WorkforceAvailabilityService availability, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var startValue = ParseInstant("start", start, errors);
        var endValue = ParseInstant("end", end, errors);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var outcome = await availability.CheckAsync(new CheckAvailability(technicianId, startValue, endValue), cancellationToken);

        return outcome switch
        {
            CheckAvailabilityOutcome.Evaluated evaluated => TypedResults.Ok(evaluated.Availability.ToResponse()),
            CheckAvailabilityOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            CheckAvailabilityOutcome.TechnicianNotFound notFound => ApiProblems.TechnicianNotFound(notFound.TechnicianId),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    /// <summary>Missing values are left to the module ("Required."); present but malformed values are reported here.</summary>
    private static DateTimeOffset? ParseInstant(string field, string? value, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // An unencoded "+" in a query string arrives as a space ("...T08:00:00 02:00"). A valid timestamp never contains
        // a space, so restoring the "+" is unambiguous.
        var text = value.Trim().Replace(' ', '+');

        if (DateTimeOffset.TryParseExact(text, _offsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
        {
            return withOffset;
        }

        if (DateTimeOffset.TryParseExact(text, _utcFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc))
        {
            return utc;
        }

        errors[field] = ["Must be an ISO 8601 date and time with an offset, e.g. 2026-03-30T08:00:00+02:00 or 2026-03-30T06:00:00Z."];
        return null;
    }

    private static TechnicianAvailabilityResponse ToResponse(this TechnicianAvailability availability) =>
        new(availability.TechnicianId,
            availability.Start,
            availability.End,
            availability.TimeZoneId,
            availability.LocalStart,
            availability.LocalEnd,
            availability.Decision.IsAvailable,
            availability.Decision.Status.ToString(),
            availability.Decision.Detail);
}
