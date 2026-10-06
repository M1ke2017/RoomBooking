using CrewCall.Contracts.OperationalCalendar;
using CrewCall.Persistence.ReadModels.OperationalCalendar;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

internal static class OperationalCalendarEndpoints
{
    public static IEndpointRouteBuilder MapOperationalCalendarEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/operational-calendar", GetAsync)
            .WithTags("Operational calendar")
            .WithName("GetOperationalCalendar");

        return app;
    }

    /// <summary>
    /// One endpoint for day, week and month views (start/end, at most 31 days). 200 with the projection; 400 for an
    /// invalid query (range, time zone, perspective); 404 when the perspective's technician, team, vehicle or site is missing.
    /// </summary>
    private static async Task<Results<Ok<OperationalCalendarResponse>, ValidationProblem, ProblemHttpResult>> GetAsync(
        string? start,
        string? end,
        string? timeZoneId,
        string? perspective,
        Guid? perspectiveId,
        OperationalCalendarService calendar,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var startValue = QueryInstant.Parse("start", start, errors);
        var endValue = QueryInstant.Parse("end", end, errors);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var outcome = await calendar.GetAsync(
            new OperationalCalendarQuery(startValue, endValue, perspective, perspectiveId, timeZoneId), cancellationToken);

        return outcome switch
        {
            OperationalCalendarOutcome.Listed listed => TypedResults.Ok(listed.Calendar.ToResponse()),
            OperationalCalendarOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            OperationalCalendarOutcome.PerspectiveNotFound notFound => ApiProblems.NotFound(
                $"{notFound.Perspective} not found", $"{notFound.Perspective} '{notFound.PerspectiveId}' does not exist."),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static OperationalCalendarResponse ToResponse(this OperationalCalendar calendar) =>
        new(
            calendar.RangeStartUtc,
            calendar.RangeEndUtc,
            calendar.RangeLocalStart,
            calendar.RangeLocalEnd,
            calendar.TimeZoneId,
            calendar.Perspective.ToString(),
            calendar.PerspectiveId,
            calendar.Items.Count,
            calendar.Items.Select(item => new OperationalCalendarItemResponse(
                item.VisitId,
                item.VisitStartUtc,
                item.VisitEndUtc,
                item.VisitLocalStart,
                item.VisitLocalEnd,
                item.VisitStatus.ToString(),
                item.WorkOrderId,
                item.WorkOrderTitle,
                item.WorkOrderPriority.ToString(),
                item.WorkOrderStatus.ToString(),
                item.CustomerId,
                item.CustomerName,
                item.SiteId,
                item.SiteName,
                item.SiteCity,
                item.AssignmentId,
                item.AssignmentStatus?.ToString(),
                item.TechnicianId,
                item.TechnicianName,
                item.TeamId,
                item.TeamName,
                item.VehicleId,
                item.VehicleName,
                item.VehicleRegistrationNumber,
                item.TravelBufferBeforeMinutes,
                item.TravelBufferAfterMinutes,
                item.Equipment.Select(asset => new OperationalCalendarEquipmentResponse(asset.EquipmentId, asset.Name, asset.AssetCode)).ToArray()))
                .ToArray());
}
