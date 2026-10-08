using CrewCall.Contracts.Reporting;
using Microsoft.Extensions.Options;

namespace CrewCall.Reporting.Api;

/// <summary>
/// The read-only Reporting API (ADR-0016): GET reports and one POST query. Nothing here changes business state, and
/// everything is answered from the reporting database alone, so it keeps working while CrewCall.Api is down.
/// </summary>
public static class ReportingEndpoints
{
    public static IEndpointRouteBuilder MapReportingEndpoints(this IEndpointRouteBuilder app)
    {
        var reporting = app.MapGroup("/api/reporting").WithTags("Reporting");

        reporting.MapGet("/technicians/{technicianId:guid}", async (
                Guid technicianId, DateOnly? start, DateOnly? end, ReportingQueries queries, IOptions<ReportingOptions> options,
                CancellationToken cancellationToken) =>
            {
                var errors = ValidateRange(start, end, options.Value.MaxReportDays, required: true);
                if (technicianId == Guid.Empty)
                {
                    errors["technicianId"] = ["Required."];
                }

                return errors.Count > 0
                    ? Results.ValidationProblem(errors)
                    : Results.Ok(await queries.TechnicianAsync(technicianId, start!.Value, end!.Value, cancellationToken));
            })
            .WithName("GetTechnicianActivityReport");

        reporting.MapPost("/technicians/summary", async (
                TechnicianSummaryRequest request, ReportingQueries queries, IOptions<ReportingOptions> options, CancellationToken cancellationToken) =>
            {
                var errors = ValidateRange(request.Start, request.End, options.Value.MaxReportDays, required: true);
                var technicianIds = request.TechnicianIds?.Distinct().ToList() ?? [];
                if (technicianIds.Count == 0)
                {
                    errors["technicianIds"] = ["At least one technician id is required."];
                }
                else if (technicianIds.Count > options.Value.MaxTechniciansPerSummary)
                {
                    errors["technicianIds"] = [$"At most {options.Value.MaxTechniciansPerSummary} technicians per summary."];
                }
                else if (technicianIds.Contains(Guid.Empty))
                {
                    errors["technicianIds"] = ["The empty id is not a technician id."];
                }

                return errors.Count > 0
                    ? Results.ValidationProblem(errors)
                    : Results.Ok(await queries.SummaryAsync(technicianIds, request.Start!.Value, request.End!.Value, cancellationToken));
            })
            .WithName("GetTechnicianActivitySummary");

        reporting.MapGet("/visits", async (
                Guid? technicianId, Guid? siteId, DateOnly? start, DateOnly? end, string? status, ReportingQueries queries,
                IOptions<ReportingOptions> options, CancellationToken cancellationToken) =>
            {
                var errors = ValidateRange(start, end, options.Value.MaxReportDays, required: false);
                return errors.Count > 0
                    ? Results.ValidationProblem(errors)
                    : Results.Ok(await queries.VisitsAsync(new VisitReportFilter(technicianId, siteId, start, end, status), cancellationToken));
            })
            .WithName("GetVisitActivityReport");

        return app;
    }

    /// <summary>An inclusive range of UTC days: both ends (when required), end not before start, at most maxDays days.</summary>
    private static Dictionary<string, string[]> ValidateRange(DateOnly? start, DateOnly? end, int maxDays, bool required)
    {
        var errors = new Dictionary<string, string[]>();
        if (required && start is null)
        {
            errors["start"] = ["Required (yyyy-MM-dd, UTC day)."];
        }

        if (required && end is null)
        {
            errors["end"] = ["Required (yyyy-MM-dd, UTC day)."];
        }

        if (start is { } from && end is { } to)
        {
            if (to < from)
            {
                errors["end"] = ["Must not be before start."];
            }
            else if (to.DayNumber - from.DayNumber + 1 > maxDays)
            {
                errors["end"] = [$"The range may cover at most {maxDays} days."];
            }
        }

        return errors;
    }
}
