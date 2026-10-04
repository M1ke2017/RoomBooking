using System.Text.Json;
using CrewCall.Contracts.Operations;
using CrewCall.Persistence.Operations;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

/// <summary>Technical, diagnostic access to the operational history. Not a reporting API.</summary>
internal static class OperationsEndpoints
{
    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/operations/events/{aggregateType}/{aggregateId:guid}", ListForAggregateAsync)
            .WithTags("Operations")
            .WithName("ListAggregateOperationalEvents");

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

    private static JsonElement ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.Clone();
    }
}
