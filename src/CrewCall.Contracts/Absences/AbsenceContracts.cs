namespace CrewCall.Contracts.Absences;

/// <summary>The technician comes from the route: POST /api/technicians/{technicianId}/absences. Half-open [Start, End).</summary>
/// <param name="Type">Vacation, SickLeave, Training or Other.</param>
public sealed record CreateAbsenceRequest(DateTimeOffset? Start, DateTimeOffset? End, string? Type, string? Reason);

/// <param name="Start">UTC.</param>
/// <param name="End">UTC.</param>
public sealed record AbsenceResponse(Guid Id, Guid TechnicianId, DateTimeOffset Start, DateTimeOffset End, string Type, string? Reason);
