namespace RoomBooking.Client.Services;

public class ActivityLogService
{
    public record LogEntry(DateTime Timestamp, string Source, string Message);

    private readonly List<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => _entries.AsReadOnly();

    public void Add(string source, string message)
    {
        _entries.Add(new LogEntry(DateTime.Now, source, message));

        if (_entries.Count > 100)
        {
            _entries.RemoveAt(0);
        }
    }

    public void Clear() => _entries.Clear();
}
