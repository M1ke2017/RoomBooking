namespace CrewCall.Contracts.Skills;

/// <param name="IsActive">Defaults to true when omitted.</param>
public sealed record CreateSkillRequest(string? Name, string? Code, bool? IsActive);

public sealed record SkillResponse(Guid Id, string Name, string? Code, bool IsActive);

public sealed record TechnicianSkillResponse(Guid TechnicianId, Guid SkillId, string SkillName, string? SkillCode);
