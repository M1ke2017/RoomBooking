using CrewCall.Contracts.Incidents;
using CrewCall.Scheduling.Incidents;
using CrewCall.WorkOrders.Incidents;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

/// <summary>
/// The urgent incident workflow (ADR-0012). Create, list, read, resolve and cancel are WorkOrders operations; analyze,
/// prepare-dispatch and dispatch are Scheduling's <see cref="UrgentIncidentService"/>. Only dispatch claims resources.
/// </summary>
internal static class IncidentEndpoints
{
    public static IEndpointRouteBuilder MapIncidentEndpoints(this IEndpointRouteBuilder app)
    {
        var incidents = app.MapGroup("/api/incidents").WithTags("Incidents");
        incidents.MapPost("/", CreateAsync).WithName("CreateIncident");
        incidents.MapGet("/", ListAsync).WithName("ListIncidents");
        incidents.MapGet("/{id:guid}", GetAsync).WithName("GetIncident");
        incidents.MapPost("/{id:guid}/analyze", AnalyzeAsync).WithName("AnalyzeIncident");
        incidents.MapPost("/{id:guid}/prepare-dispatch", PrepareDispatchAsync).WithName("PrepareIncidentDispatch");
        incidents.MapPost("/{id:guid}/dispatch", DispatchAsync).WithName("DispatchIncident");
        incidents.MapPost("/{id:guid}/resolve", ResolveAsync).WithName("ResolveIncident");
        incidents.MapPost("/{id:guid}/cancel", CancelAsync).WithName("CancelIncident");

        return app;
    }

    private static async Task<Results<Created<IncidentResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateIncidentRequest request, IncidentService incidents, CancellationToken cancellationToken)
    {
        var outcome = await incidents.CreateAsync(
            new CreateIncident(
                request.CustomerId, request.SiteId, request.Title, request.Description, request.Priority,
                request.RequestedStart, request.RequestedEnd, request.RequiredSkillCodes),
            cancellationToken);

        return outcome switch
        {
            CreateIncidentOutcome.Created created => TypedResults.Created($"/api/incidents/{created.Incident.Id}", created.Incident.ToResponse()),
            CreateIncidentOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            CreateIncidentOutcome.CustomerNotFound notFound => ApiProblems.NotFound("Customer not found", $"Customer '{notFound.CustomerId}' does not exist."),
            CreateIncidentOutcome.SiteNotFound notFound => ApiProblems.NotFound("Site not found", $"Site '{notFound.SiteId}' does not exist."),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    /// <summary>Newest first (CreatedAtUtc descending, then id); optional status and priority filters.</summary>
    private static async Task<Results<Ok<IncidentResponse[]>, ValidationProblem>> ListAsync(
        string? status, string? priority, IncidentService incidents, CancellationToken cancellationToken)
    {
        var outcome = await incidents.ListAsync(new ListIncidents(status, priority), cancellationToken);

        return outcome switch
        {
            ListIncidentsOutcome.Listed listed => TypedResults.Ok(listed.Incidents.Select(incident => incident.ToResponse()).ToArray()),
            ListIncidentsOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static async Task<Results<Ok<IncidentResponse>, ProblemHttpResult>> GetAsync(
        Guid id, IncidentService incidents, CancellationToken cancellationToken)
    {
        var incident = await incidents.GetAsync(id, cancellationToken);
        return incident is null ? IncidentNotFound(id) : TypedResults.Ok(incident.ToResponse());
    }

    /// <summary>200 with the suggestions (advice only); 409 for a dispatched, resolved or cancelled incident.</summary>
    private static async Task<Results<Ok<IncidentAnalysisResponse>, ValidationProblem, ProblemHttpResult>> AnalyzeAsync(
        Guid id, AnalyzeIncidentRequest? request, UrgentIncidentService workflow, CancellationToken cancellationToken)
    {
        var outcome = await workflow.AnalyzeAsync(
            new AnalyzeIncident(id, request?.CandidateTechnicianIds, request?.PreferredTeamId), cancellationToken);

        return outcome switch
        {
            AnalyzeIncidentOutcome.Analyzed analyzed => TypedResults.Ok(analyzed.Analysis.ToResponse()),
            AnalyzeIncidentOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            AnalyzeIncidentOutcome.IncidentNotFound => IncidentNotFound(id),
            AnalyzeIncidentOutcome.NotFound notFound => ApiProblems.NotFound(
                "Not found", "These do not exist: " + string.Join(", ", notFound.Missing.Select(m => $"{m.Kind} '{m.Id}'")) + "."),
            AnalyzeIncidentOutcome.NotAllowed notAllowed => ApiProblems.Conflict(
                "Incident cannot be analysed", $"Incident '{id}' is {notAllowed.Status}; only incidents not dispatched yet can be analysed."),
            AnalyzeIncidentOutcome.ConcurrentChange => ConcurrentChange(id),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    /// <summary>200 also when CanDispatch is false: the answer is advice. Nothing is written.</summary>
    private static async Task<Results<Ok<PrepareIncidentDispatchResponse>, ValidationProblem, ProblemHttpResult>> PrepareDispatchAsync(
        Guid id, PrepareIncidentDispatchRequest request, UrgentIncidentService workflow, CancellationToken cancellationToken)
    {
        var outcome = await workflow.PrepareDispatchAsync(
            new IncidentResourceSelection(
                id, request.TechnicianId, request.VehicleId, request.EquipmentIds, request.TravelBufferBeforeMinutes, request.TravelBufferAfterMinutes),
            cancellationToken);

        return outcome switch
        {
            IncidentDispatchOutcome.Prepared prepared => TypedResults.Ok(prepared.Readiness.ToResponse()),
            IncidentDispatchOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            _ => ToProblem(id, outcome)
        };
    }

    /// <summary>
    /// 201 Created with what dispatch created (Location: the new work order). 409 when the incident is not ready, was
    /// already dispatched, or the resources are taken (final check or a lost race); nothing is written then.
    /// </summary>
    private static async Task<Results<Created<IncidentDispatchResponse>, ValidationProblem, ProblemHttpResult>> DispatchAsync(
        Guid id, DispatchIncidentRequest request, UrgentIncidentService workflow, IncidentService incidents, CancellationToken cancellationToken)
    {
        var outcome = await workflow.DispatchIncidentAsync(
            new IncidentResourceSelection(
                id, request.TechnicianId, request.VehicleId, request.EquipmentIds, request.TravelBufferBeforeMinutes, request.TravelBufferAfterMinutes),
            cancellationToken);

        switch (outcome)
        {
            case IncidentDispatchOutcome.Dispatched dispatched:
                var incident = await incidents.GetAsync(id, cancellationToken)
                    ?? throw new InvalidOperationException($"Incident '{id}' disappeared after dispatch.");
                return TypedResults.Created(
                    $"/api/work-orders/{dispatched.WorkOrderId}",
                    new IncidentDispatchResponse(incident.ToResponse(), dispatched.WorkOrderId, dispatched.VisitId, dispatched.Assignment.ToResponse()));
            case IncidentDispatchOutcome.Invalid invalid:
                return TypedResults.ValidationProblem(invalid.Errors);
            default:
                return ToProblem(id, outcome);
        }
    }

    /// <summary>Dispatched → Resolved. The work order and visit are not completed automatically.</summary>
    private static async Task<Results<Ok<IncidentResponse>, ProblemHttpResult>> ResolveAsync(
        Guid id, IncidentService incidents, CancellationToken cancellationToken) =>
        ToResult(id, await incidents.ResolveAsync(id, cancellationToken));

    /// <summary>Before dispatch only; a dispatched incident gets 409 and nothing downstream is touched.</summary>
    private static async Task<Results<Ok<IncidentResponse>, ProblemHttpResult>> CancelAsync(
        Guid id, IncidentService incidents, CancellationToken cancellationToken) =>
        ToResult(id, await incidents.CancelAsync(id, cancellationToken));

    private static Results<Ok<IncidentResponse>, ProblemHttpResult> ToResult(Guid id, ChangeIncidentOutcome outcome) => outcome switch
    {
        ChangeIncidentOutcome.Changed changed => TypedResults.Ok(changed.Incident.ToResponse()),
        ChangeIncidentOutcome.NotFound => IncidentNotFound(id),
        ChangeIncidentOutcome.TransitionNotAllowed notAllowed => ApiProblems.Conflict(
            "Status transition not allowed", $"An incident cannot move from {notAllowed.From} to {notAllowed.To}."),
        ChangeIncidentOutcome.ConcurrentChange => ConcurrentChange(id),
        _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
    };

    /// <summary>404 for a missing incident or resource; 409 for lifecycle conflicts, duplicates and taken resources.</summary>
    private static ProblemHttpResult ToProblem(Guid id, IncidentDispatchOutcome outcome) => outcome switch
    {
        IncidentDispatchOutcome.IncidentNotFound => IncidentNotFound(id),
        IncidentDispatchOutcome.ResourcesNotFound notFound => ApiProblems.NotFound(
            "Resource not found",
            "These resources do not exist: "
            + string.Join(", ", notFound.Missing.Select(resource => $"{resource.Type} '{resource.ResourceId}'")) + "."),
        IncidentDispatchOutcome.NotReady notReady => ApiProblems.Conflict(
            "Incident not ready for dispatch", $"Incident '{id}' is {notReady.Status}; it must be analysed (ReadyForDispatch) first."),
        IncidentDispatchOutcome.AlreadyDispatched dispatched => ApiProblems.Conflict(
            "Incident already dispatched", $"Incident '{id}' was already dispatched to work order '{dispatched.WorkOrderId}'."),
        IncidentDispatchOutcome.Rejected rejected => TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Resources not available",
            detail: string.Join(" ", rejected.Reasons.Select(reason => reason.Message)),
            extensions: new Dictionary<string, object?>
            {
                ["reasons"] = rejected.Reasons.Select(SchedulingEndpoints.ToConflictResponse).ToArray(),
                ["impact"] = rejected.Impact.Select(ToResponse).ToArray()
            }),
        _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
    };

    private static ProblemHttpResult IncidentNotFound(Guid id) =>
        ApiProblems.NotFound("Incident not found", $"Incident '{id}' does not exist.");

    private static ProblemHttpResult ConcurrentChange(Guid id) =>
        ApiProblems.Conflict("Concurrent change", $"Incident '{id}' was changed by another request. Reload and retry.");

    private static IncidentResponse ToResponse(this Incident incident) =>
        new(
            incident.Id,
            incident.CustomerId,
            incident.SiteId,
            incident.Title,
            incident.Description,
            incident.Priority.ToString(),
            incident.Status.ToString(),
            incident.RequestedStart,
            incident.RequestedEnd,
            incident.RequiredSkills.Select(skill => skill.SkillCode).Order().ToArray(),
            incident.WorkOrderId,
            incident.CreatedAtUtc,
            incident.UpdatedAtUtc,
            incident.ResolvedAtUtc);

    private static IncidentAnalysisResponse ToResponse(this IncidentAnalysis analysis) =>
        new(
            analysis.IncidentId,
            analysis.IncidentStatus.ToString(),
            analysis.GeneratedAtUtc,
            analysis.CandidatesEvaluated,
            analysis.CandidatesTruncated,
            analysis.EligibleCount,
            analysis.RejectedCount,
            analysis.Suggestions.Select(suggestion => new IncidentDispatchSuggestionResponse(
                suggestion.Rank,
                suggestion.TechnicianId,
                suggestion.TechnicianName,
                suggestion.TeamId,
                suggestion.SuggestionType.ToString(),
                suggestion.Score,
                suggestion.SchedulingChecked,
                suggestion.Reasons.Select(SchedulingEndpoints.ToConflictResponse).ToArray(),
                suggestion.FirstConflict?.ConflictingVisitId,
                suggestion.FirstConflict?.ConflictingWorkOrderId,
                suggestion.FirstConflict?.ReservedStart,
                suggestion.FirstConflict?.ReservedEnd,
                suggestion.Impact.Select(ToResponse).ToArray())).ToArray());

    private static PrepareIncidentDispatchResponse ToResponse(this IncidentDispatchReadiness readiness)
    {
        var check = readiness.Check;
        return new(
            readiness.IncidentId,
            readiness.CanDispatch,
            check.Reasons.Select(SchedulingEndpoints.ToConflictResponse).ToArray(),
            readiness.Impact.Select(ToResponse).ToArray(),
            check.TechnicianId,
            check.VehicleId,
            check.EquipmentIds.ToArray(),
            check.Start,
            check.End,
            check.EffectiveStart,
            check.EffectiveEnd);
    }

    private static IncidentImpactResponse ToResponse(DispatchImpact impact) =>
        new(
            impact.ReservationId,
            impact.ResourceType.ToString(),
            impact.ResourceId,
            impact.ConflictingVisitId,
            impact.ConflictingWorkOrderId,
            impact.ReservedStart,
            impact.ReservedEnd,
            impact.WithinTravelBuffer);
}
