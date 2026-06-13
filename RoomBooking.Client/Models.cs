namespace RoomBooking.Client.Models;

public enum ReservationStatus
{
    Active = 0,
    Cancelled = 1,
    Finished = 2
}

public class Room
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Capacity { get; set; }
}

public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
}

public class Reservation
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public int UserId { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public ReservationStatus Status { get; set; } = ReservationStatus.Active;
    public string? ExternalCalendarEventId { get; set; }
}
