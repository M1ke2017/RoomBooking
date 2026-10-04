using CrewCall.Contracts.Holidays;
using CrewCall.Workforce.Holidays;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

internal static class HolidayEndpoints
{
    public static IEndpointRouteBuilder MapHolidayEndpoints(this IEndpointRouteBuilder app)
    {
        var holidays = app.MapGroup("/api/holidays").WithTags("Holidays");
        holidays.MapPost("/", CreateAsync).WithName("CreateHoliday");
        holidays.MapGet("/", ListAsync).WithName("ListHolidays");

        return app;
    }

    private static async Task<Results<Created<HolidayResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateHolidayRequest request, HolidayService holidays, CancellationToken cancellationToken)
    {
        var outcome = await holidays.CreateAsync(
            new CreateHoliday(request.Date, request.Name, request.CountryCode, request.RegionCode), cancellationToken);

        return outcome switch
        {
            CreateHolidayOutcome.Created created => TypedResults.Created((string?)null, created.Holiday.ToResponse()),
            CreateHolidayOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            CreateHolidayOutcome.AlreadyExists duplicate => ApiProblems.Conflict(
                "Holiday already exists",
                $"Holiday '{duplicate.ExistingHolidayId}' already covers this country, region and date."),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    /// <summary>GET /api/holidays, optionally filtered: ?countryCode=PL.</summary>
    private static async Task<Results<Ok<HolidayResponse[]>, ValidationProblem>> ListAsync(
        string? countryCode, HolidayService holidays, CancellationToken cancellationToken)
    {
        var outcome = await holidays.ListAsync(countryCode, cancellationToken);

        return outcome switch
        {
            ListHolidaysOutcome.Listed listed => TypedResults.Ok(listed.Holidays.Select(holiday => holiday.ToResponse()).ToArray()),
            ListHolidaysOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static HolidayResponse ToResponse(this HolidayCalendarEntry holiday) =>
        new(holiday.Id, holiday.Date, holiday.Name, holiday.CountryCode, holiday.RegionCode);
}
