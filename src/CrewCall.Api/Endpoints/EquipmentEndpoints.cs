using CrewCall.Contracts.Equipment;
using CrewCall.Resources.Equipment;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

internal static class EquipmentEndpoints
{
    public static IEndpointRouteBuilder MapEquipmentEndpoints(this IEndpointRouteBuilder app)
    {
        var equipment = app.MapGroup("/api/equipment").WithTags("Equipment");
        equipment.MapPost("/", CreateAsync).WithName("CreateEquipment");
        equipment.MapGet("/", ListAsync).WithName("ListEquipment");

        return app;
    }

    private static async Task<Results<Created<EquipmentResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateEquipmentRequest request, EquipmentService equipment, CancellationToken cancellationToken)
    {
        var outcome = await equipment.CreateAsync(new CreateEquipment(request.Name, request.AssetCode, request.IsActive), cancellationToken);

        return outcome switch
        {
            CreateEquipmentOutcome.Created created => TypedResults.Created((string?)null, created.Equipment.ToResponse()),
            CreateEquipmentOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            CreateEquipmentOutcome.AssetCodeAlreadyExists duplicate => ApiProblems.Conflict(
                "Asset code already exists", $"Equipment with asset code '{duplicate.AssetCode}' already exists."),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static async Task<Ok<EquipmentResponse[]>> ListAsync(EquipmentService equipment, CancellationToken cancellationToken)
    {
        var list = await equipment.ListAsync(cancellationToken);
        return TypedResults.Ok(list.Select(item => item.ToResponse()).ToArray());
    }

    internal static EquipmentResponse ToResponse(this EquipmentItem equipment) =>
        new(equipment.Id, equipment.Name, equipment.AssetCode, equipment.IsActive);
}
