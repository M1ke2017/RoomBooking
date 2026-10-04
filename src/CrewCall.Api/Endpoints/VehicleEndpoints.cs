using CrewCall.Contracts.Vehicles;
using CrewCall.Resources.Vehicles;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

internal static class VehicleEndpoints
{
    public static IEndpointRouteBuilder MapVehicleEndpoints(this IEndpointRouteBuilder app)
    {
        var vehicles = app.MapGroup("/api/vehicles").WithTags("Vehicles");
        vehicles.MapPost("/", CreateAsync).WithName("CreateVehicle");
        vehicles.MapGet("/", ListAsync).WithName("ListVehicles");

        return app;
    }

    private static async Task<Results<Created<VehicleResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateVehicleRequest request, VehicleService vehicles, CancellationToken cancellationToken)
    {
        var outcome = await vehicles.CreateAsync(
            new CreateVehicle(request.RegistrationNumber, request.DisplayName, request.VehicleType, request.IsActive), cancellationToken);

        return outcome switch
        {
            CreateVehicleOutcome.Created created => TypedResults.Created((string?)null, created.Vehicle.ToResponse()),
            CreateVehicleOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            CreateVehicleOutcome.RegistrationNumberAlreadyExists duplicate => ApiProblems.Conflict(
                "Registration number already exists", $"A vehicle with registration number '{duplicate.RegistrationNumber}' already exists."),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static async Task<Ok<VehicleResponse[]>> ListAsync(VehicleService vehicles, CancellationToken cancellationToken)
    {
        var list = await vehicles.ListAsync(cancellationToken);
        return TypedResults.Ok(list.Select(vehicle => vehicle.ToResponse()).ToArray());
    }

    internal static VehicleResponse ToResponse(this Vehicle vehicle) =>
        new(vehicle.Id, vehicle.RegistrationNumber, vehicle.DisplayName, vehicle.VehicleType, vehicle.IsActive);
}
