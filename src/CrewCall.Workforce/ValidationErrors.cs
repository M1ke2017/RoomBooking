namespace CrewCall.Workforce;

/// <summary>Collects field validation errors and normalizes text input (trimmed; blank becomes null).</summary>
internal sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = [];

    public bool Any => _errors.Count > 0;

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var messages))
        {
            _errors[field] = messages = [];
        }

        messages.Add(message);
    }

    public string? Required(string field, string? value, int maxLength)
    {
        var text = Normalize(value);
        if (text is null)
        {
            Add(field, "Required.");
        }
        else
        {
            CheckLength(field, text, maxLength);
        }

        return text;
    }

    public string? Optional(string field, string? value, int maxLength)
    {
        var text = Normalize(value);
        if (text is not null)
        {
            CheckLength(field, text, maxLength);
        }

        return text;
    }

    public IReadOnlyDictionary<string, string[]> ToDictionary() =>
        _errors.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray());

    private void CheckLength(string field, string text, int maxLength)
    {
        if (text.Length > maxLength)
        {
            Add(field, $"Must be at most {maxLength} characters.");
        }
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
