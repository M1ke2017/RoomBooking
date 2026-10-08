using CrewCall.Workforce.TimeZones;

namespace CrewCall.Workforce.Technicians;

/// <summary>
/// The basic identity of a field technician, with optional membership of one team, and the time context their working
/// hours and holidays are interpreted in.
/// </summary>
public sealed class Technician
{
    public const int DisplayNameMaxLength = 200;
    public const int EmailMaxLength = 254;

    /// <summary>E.164: "+" and at most 15 digits.</summary>
    public const int PhoneNumberMaxLength = 16;

    public const int TimeZoneIdMaxLength = TechnicianTimeContext.TimeZoneIdMaxLength;
    public const int CountryCodeLength = TechnicianTimeContext.CountryCodeLength;

    internal Technician(
        Guid id, string displayName, string email, bool isActive, string timeZoneId, string countryCode, string? phoneNumber = null)
    {
        Id = id;
        DisplayName = displayName;
        Email = email;
        IsActive = isActive;
        TimeZoneId = timeZoneId;
        CountryCode = countryCode;
        PhoneNumber = phoneNumber;
    }

    public Guid Id { get; private set; }

    public string DisplayName { get; private set; }

    /// <summary>Unique, stored in lower case.</summary>
    public string Email { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>IANA time zone id (e.g. "Europe/Warsaw"): working hours are local wall-clock times in this zone.</summary>
    public string TimeZoneId { get; private set; }

    /// <summary>ISO 3166-1 alpha-2, upper case: selects the national holiday calendar that applies.</summary>
    public string CountryCode { get; private set; }

    /// <summary>
    /// Contact number in E.164 form (e.g. "+48601234567"), if known: the foundation for contacting the technician about
    /// an urgent change. Nothing sends messages yet.
    /// </summary>
    public string? PhoneNumber { get; private set; }

    /// <summary>The team the technician currently belongs to, if any (0..1).</summary>
    public Guid? TeamId { get; private set; }

    internal void JoinTeam(Guid teamId) => TeamId = teamId;

    internal void LeaveTeam() => TeamId = null;
}
