using CrewCall.Contracts.WorkingHours;
using CrewCall.Workforce.WorkingHours;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

internal static class WorkingHoursEndpoints
{
    public static IEndpointRouteBuilder MapWorkingHoursEndpoints(this IEndpointRouteBuilder app)
    {
        var workingHours = app.MapGroup("/api/technicians/{technicianId:guid}/working-hours").WithTags("Working hours");
        workingHours.MapPost("/", CreateAsync).WithName("CreateWorkingHours");
        workingHours.MapGet("/", ListAsync).WithName("ListWorkingHours");
        workingHours.MapDelete("/{workingHoursId:guid}", DeleteAsync).WithName("DeleteWorkingHours");

        return app;
    }

    private static async Task<Results<Created<WorkingHoursResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid technicianId, CreateWorkingHoursRequest request, WorkingHoursService workingHours, CancellationToken cancellationToken)
    {
        var outcome = await workingHours.CreateAsync(
            new CreateWorkingHours(technicianId, request.DayOfWeek, request.StartLocalTime, request.EndLocalTime), cancellationToken);

        return outcome switch
        {
            CreateWorkingHoursOutcome.Created created => TypedResults.Created(
                $"/api/technicians/{technicianId}/working-hours/{created.WorkingHours.Id}", created.WorkingHours.ToResponse()),
            CreateWorkingHoursOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            CreateWorkingHoursOutcome.TechnicianNotFound notFound => ApiProblems.TechnicianNotFound(notFound.TechnicianId),
            CreateWorkingHoursOutcome.Overlaps overlap => ApiProblems.Conflict(
                "Working hours overlap",
                $"The range overlaps working hours '{overlap.ExistingWorkingHoursId}' of the same technician on the same day."),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static async Task<Results<Ok<WorkingHoursResponse[]>, ProblemHttpResult>> ListAsync(
        Guid technicianId, WorkingHoursService workingHours, CancellationToken cancellationToken)
    {
        var list = await workingHours.ListAsync(technicianId, cancellationToken);

        return list is null
            ? ApiProblems.TechnicianNotFound(technicianId)
            : TypedResults.Ok(list.Select(range => range.ToResponse()).ToArray());
    }

    /// <summary>Idempotent: 204 also when the range does not exist.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid technicianId, Guid workingHoursId, WorkingHoursService workingHours, CancellationToken cancellationToken)
    {
        var outcome = await workingHours.DeleteAsync(technicianId, workingHoursId, cancellationToken);

        return outcome switch
        {
            DeleteWorkingHoursOutcome.Deleted => TypedResults.NoContent(),
            DeleteWorkingHoursOutcome.TechnicianNotFound notFound => ApiProblems.TechnicianNotFound(notFound.TechnicianId),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static WorkingHoursResponse ToResponse(this TechnicianWorkingHours range) =>
        new(range.Id, range.TechnicianId, range.DayOfWeek.ToString(),
            LocalTimeText.FormatStart(range.StartLocalTime), LocalTimeText.FormatEnd(range.EndLocalTime));
}
