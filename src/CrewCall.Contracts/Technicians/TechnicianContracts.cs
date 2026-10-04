namespace CrewCall.Contracts.Technicians;

/// <param name="IsActive">Defaults to true when omitted.</param>
public sealed record CreateTechnicianRequest(string? DisplayName, string? Email, bool? IsActive);

public sealed record TechnicianResponse(Guid Id, string DisplayName, string Email, bool IsActive);
