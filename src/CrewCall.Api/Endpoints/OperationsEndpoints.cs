using System.Text.Json;
using CrewCall.Api.Operations;
using CrewCall.Contracts.Operations;
using CrewCall.Persistence.Operations;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

/// <summary>
/// The manager's operations board, and technical, diagnostic access to the operational history (not a reporting API).
/// </summary>
internal static class OperationsEndpoints
{
    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/operations/events/{aggregateType}/{aggregateId:guid}", ListForAggregateAsync)
            .WithTags("Operations")
            .WithName("ListAggregateOperationalEvents");

        app.MapGet("/api/operations/board", GetBoardAsync)
            .WithTags("Operations")
            .WithName("GetOperationsBoard");

        return app;
    }

    /// <summary>The aggregate's events, oldest first. An aggregate without events returns an empty list.</summary>
    private static async Task<Ok<OperationalEventResponse[]>> ListForAggregateAsync(
        string aggregateType, Guid aggregateId, OperationalEventLog log, CancellationToken cancellationToken)
    {
        var events = await log.ListForAggregateAsync(aggregateType, aggregateId, cancellationToken);

        return TypedResults.Ok(events.Select(e => new OperationalEventResponse(
            e.Id,
            e.OccurredAtUtc,
            e.EventType,
            e.AggregateType,
            e.AggregateId,
            ParsePayload(e.PayloadJson),
            e.CorrelationId)).ToArray());
    }

    /// <summary>200 with the day's board (today in Europe/Warsaw by default); 400 for an invalid date or time zone.</summary>
    private static async Task<Results<Ok<OperationsBoardResponse>, ValidationProblem>> GetBoardAsync(
        string? date, string? timeZoneId, OperationsBoardService board, CancellationToken cancellationToken)
    {
        var (response, errors) = await board.GetAsync(date, timeZoneId, cancellationToken);
        return response is null ? TypedResults.ValidationProblem(errors!) : TypedResults.Ok(response);
    }

    private static JsonElement ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.Clone();
    }
}
