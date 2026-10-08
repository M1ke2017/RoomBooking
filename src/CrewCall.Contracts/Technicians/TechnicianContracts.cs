namespace CrewCall.Contracts.Technicians;

/// <param name="IsActive">Defaults to true when omitted.</param>
/// <param name="TimeZoneId">IANA time zone id, e.g. "Europe/Warsaw". Required.</param>
/// <param name="CountryCode">ISO 3166-1 alpha-2, e.g. "PL". Required.</param>
/// <param name="PhoneNumber">Optional, E.164, e.g. "+48601234567" (spaces, dashes, dots and brackets are accepted and removed).</param>
public sealed record CreateTechnicianRequest(
    string? DisplayName, string? Email, bool? IsActive, string? TimeZoneId, string? CountryCode, string? PhoneNumber = null);

public sealed record TechnicianResponse(
    Guid Id,
    string DisplayName,
    string Email,
    bool IsActive,
    Guid? TeamId,
    string TimeZoneId,
    string CountryCode,
    string? PhoneNumber);
