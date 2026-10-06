using CrewCall.Contracts.Scheduling;
using CrewCall.Scheduling.Checks;
using CrewCall.Scheduling.Matching;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

internal static class SchedulingEndpoints
{
    public static IEndpointRouteBuilder MapSchedulingEndpoints(this IEndpointRouteBuilder app)
    {
        var scheduling = app.MapGroup("/api/scheduling").WithTags("Scheduling");
        scheduling.MapPost("/check", CheckAsync).WithName("CheckScheduling");
        scheduling.MapPost("/match", MatchAsync).WithName("MatchResources");

        return app;
    }

    /// <summary>
    /// 200 for every completed evaluation, feasible or not; 400 for an invalid request; 404 when a technician, vehicle or
    /// equipment asset does not exist. Never 409: the check reserves nothing.
    /// </summary>
    private static async Task<Results<Ok<SchedulingCheckResponse>, ValidationProblem, ProblemHttpResult>> CheckAsync(
        SchedulingCheckRequest request, SchedulingCheckService checks, CancellationToken cancellationToken)
    {
        var outcome = await checks.CheckAsync(
            new SchedulingCheck(
                request.VisitId,
                request.TechnicianId,
                request.VehicleId,
                request.EquipmentIds,
                request.Start,
                request.End,
                request.RequiredSkillCodes,
                request.TravelBufferBeforeMinutes,
                request.TravelBufferAfterMinutes),
            cancellationToken);

        return outcome switch
        {
            SchedulingCheckOutcome.Checked checkedOutcome => TypedResults.Ok(checkedOutcome.Result.ToResponse()),
            SchedulingCheckOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            SchedulingCheckOutcome.ResourcesNotFound notFound => ApiProblems.NotFound(
                "Resource not found",
                "These resources do not exist: "
                + string.Join(", ", notFound.Missing.Select(resource => $"{resource.Type} '{resource.ResourceId}'")) + "."),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    /// <summary>
    /// 200 with eligible candidates (best first, explained) and rejected ones (with reasons); 400 for an invalid request;
    /// 404 when a requested technician, team, vehicle or equipment asset does not exist. Writes nothing.
    /// </summary>
    private static async Task<Results<Ok<ResourceMatchingResponse>, ValidationProblem, ProblemHttpResult>> MatchAsync(
        ResourceMatchingRequest request, ResourceMatchingService matching, CancellationToken cancellationToken)
    {
        var outcome = await matching.MatchAsync(
            new ResourceMatching(
                request.Start,
                request.End,
                request.RequiredSkillCodes,
                request.PreferredTeamId,
                request.CandidateTechnicianIds,
                request.MaxResults,
                request.VehicleId,
                request.EquipmentIds,
                request.VisitId,
                request.WorkloadWindowStart,
                request.WorkloadWindowEnd),
            cancellationToken);

        return outcome switch
        {
            ResourceMatchingOutcome.Matched matched => TypedResults.Ok(ToResponse(matched.Result)),
            ResourceMatchingOutcome.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            ResourceMatchingOutcome.NotFound notFound => ApiProblems.NotFound(
                "Resource not found",
                "These do not exist: " + string.Join(", ", notFound.Missing.Select(missing => $"{missing.Kind} '{missing.Id}'")) + "."),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static ResourceMatchingResponse ToResponse(ResourceMatchingResult result) =>
        new(
            result.Start,
            result.End,
            result.WorkloadWindowStart,
            result.WorkloadWindowEnd,
            result.CandidatesEvaluated,
            result.CandidatesTruncated,
            result.TotalEligible,
            result.TotalRejected,
            result.EligibleCandidates.Select(candidate => new EligibleCandidateResponse(
                candidate.Rank,
                candidate.TechnicianId,
                candidate.TechnicianName,
                candidate.TeamId,
                candidate.TotalScore,
                new ScoreComponentsResponse(
                    candidate.Components.SkillScore, candidate.Components.AvailabilityScore,
                    candidate.Components.WorkloadScore, candidate.Components.TeamPreferenceScore),
                new CandidateWorkloadResponse(candidate.Workload.AssignedMinutes, candidate.Workload.AssignmentCount),
                new SkillCoverageResponse(candidate.SkillCoverage.RequiredCount, candidate.SkillCoverage.MatchedCount, candidate.SkillCoverage.AdditionalCount),
                candidate.InPreferredTeam)).ToArray(),
            result.RejectedCandidates.Select(candidate => new RejectedCandidateResponse(
                candidate.TechnicianId,
                candidate.TechnicianName,
                candidate.TeamId,
                candidate.Reasons.Select(ToConflictResponse).ToArray())).ToArray());

    internal static SchedulingConflictResponse ToConflictResponse(SchedulingConflict reason) =>
        new(
            reason.Code.ToString(),
            reason.Message,
            reason.ResourceType?.ToString(),
            reason.RelatedResourceId,
            reason.ReservationId,
            reason.ReservedStart,
            reason.ReservedEnd,
            reason.Details.ToArray(),
            reason.ReservationVisitId);

    private static SchedulingCheckResponse ToResponse(this SchedulingCheckResult result) =>
        new(
            result.IsFeasible,
            result.Reasons.Select(ToConflictResponse).ToArray(),
            result.TechnicianId,
            result.VehicleId,
            result.EquipmentIds.ToArray(),
            result.VisitId,
            result.Start,
            result.End,
            result.EffectiveStart,
            result.EffectiveEnd);
}
