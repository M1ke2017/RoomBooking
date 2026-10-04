namespace CrewCall.Contracts.Teams;

/// <param name="IsActive">Defaults to true when omitted.</param>
public sealed record CreateTeamRequest(string? Name, bool? IsActive);

public sealed record TeamResponse(Guid Id, string Name, bool IsActive);

public sealed record TeamTechnicianResponse(Guid TeamId, Guid TechnicianId, string DisplayName, string Email, bool IsActive);
