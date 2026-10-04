using CrewCall.Contracts.Technicians;
using CrewCall.Workforce.Technicians;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

internal static class TechnicianEndpoints
{
    public static IEndpointRouteBuilder MapTechnicianEndpoints(this IEndpointRouteBuilder app)
    {
        var technicians = app.MapGroup("/api/technicians").WithTags("Technicians");

        technicians.MapPost("/", CreateAsync).WithName("CreateTechnician");
        technicians.MapGet("/", ListAsync).WithName("ListTechnicians");

        return app;
    }

    private static async Task<Results<Created<TechnicianResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateTechnicianRequest request, TechnicianService technicians, CancellationToken cancellationToken)
    {
        var outcome = await technicians.CreateAsync(
            new CreateTechnician(request.DisplayName, request.Email, request.IsActive), cancellationToken);

        return outcome switch
        {
            CreateTechnicianOutcome.Created created => TypedResults.Created((string?)null, created.Technician.ToResponse()),
            CreateTechnicianOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            CreateTechnicianOutcome.EmailAlreadyExists duplicate => ApiProblems.Conflict(
                "Technician email already exists", $"A technician with email '{duplicate.Email}' already exists."),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static async Task<Ok<TechnicianResponse[]>> ListAsync(TechnicianService technicians, CancellationToken cancellationToken)
    {
        var list = await technicians.ListAsync(cancellationToken);
        return TypedResults.Ok(list.Select(technician => technician.ToResponse()).ToArray());
    }

    internal static TechnicianResponse ToResponse(this Technician technician) =>
        new(technician.Id, technician.DisplayName, technician.Email, technician.IsActive, technician.TeamId);
}
