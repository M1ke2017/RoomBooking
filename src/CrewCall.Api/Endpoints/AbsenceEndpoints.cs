using CrewCall.Contracts.Absences;
using CrewCall.Workforce.Absences;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

internal static class AbsenceEndpoints
{
    public static IEndpointRouteBuilder MapAbsenceEndpoints(this IEndpointRouteBuilder app)
    {
        var absences = app.MapGroup("/api/technicians/{technicianId:guid}/absences").WithTags("Absences");
        absences.MapPost("/", CreateAsync).WithName("CreateAbsence");
        absences.MapGet("/", ListAsync).WithName("ListAbsences");
        absences.MapDelete("/{absenceId:guid}", DeleteAsync).WithName("DeleteAbsence");

        return app;
    }

    private static async Task<Results<Created<AbsenceResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid technicianId, CreateAbsenceRequest request, AbsenceService absences, CancellationToken cancellationToken)
    {
        var outcome = await absences.CreateAsync(
            new CreateAbsence(technicianId, request.Start, request.End, request.Type, request.Reason), cancellationToken);

        return outcome switch
        {
            CreateAbsenceOutcome.Created created => TypedResults.Created(
                $"/api/technicians/{technicianId}/absences/{created.Absence.Id}", created.Absence.ToResponse()),
            CreateAbsenceOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            CreateAbsenceOutcome.TechnicianNotFound notFound => ApiProblems.TechnicianNotFound(notFound.TechnicianId),
            CreateAbsenceOutcome.Overlaps overlap => ApiProblems.Conflict(
                "Absence overlaps", $"The period overlaps absence '{overlap.ExistingAbsenceId}' of the same technician."),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static async Task<Results<Ok<AbsenceResponse[]>, ProblemHttpResult>> ListAsync(
        Guid technicianId, AbsenceService absences, CancellationToken cancellationToken)
    {
        var list = await absences.ListAsync(technicianId, cancellationToken);

        return list is null
            ? ApiProblems.TechnicianNotFound(technicianId)
            : TypedResults.Ok(list.Select(absence => absence.ToResponse()).ToArray());
    }

    /// <summary>Idempotent: 204 also when the absence does not exist.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid technicianId, Guid absenceId, AbsenceService absences, CancellationToken cancellationToken)
    {
        var outcome = await absences.DeleteAsync(technicianId, absenceId, cancellationToken);

        return outcome switch
        {
            DeleteAbsenceOutcome.Deleted => TypedResults.NoContent(),
            DeleteAbsenceOutcome.TechnicianNotFound notFound => ApiProblems.TechnicianNotFound(notFound.TechnicianId),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static AbsenceResponse ToResponse(this TechnicianAbsence absence) =>
        new(absence.Id, absence.TechnicianId, absence.Start, absence.End, absence.Type.ToString(), absence.Reason);
}
