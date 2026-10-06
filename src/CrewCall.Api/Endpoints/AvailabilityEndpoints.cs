using CrewCall.Contracts.Availability;
using CrewCall.Workforce.Availability;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

internal static class AvailabilityEndpoints
{
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
        var startValue = QueryInstant.Parse("start", start, errors);
        var endValue = QueryInstant.Parse("end", end, errors);
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
