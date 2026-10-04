namespace CrewCall.Contracts.WorkingHours;

/// <summary>The technician comes from the route: POST /api/technicians/{technicianId}/working-hours.</summary>
/// <param name="DayOfWeek">English day name, e.g. "Monday".</param>
/// <param name="StartLocalTime">"HH:mm" in the technician's time zone.</param>
/// <param name="EndLocalTime">"HH:mm"; "24:00" is the end of the day. A shift crossing midnight is two ranges.</param>
public sealed record CreateWorkingHoursRequest(string? DayOfWeek, string? StartLocalTime, string? EndLocalTime);

/// <param name="EndLocalTime">"24:00" when the range runs to midnight.</param>
public sealed record WorkingHoursResponse(Guid Id, Guid TechnicianId, string DayOfWeek, string StartLocalTime, string EndLocalTime);
