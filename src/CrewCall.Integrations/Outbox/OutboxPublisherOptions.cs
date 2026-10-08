namespace CrewCall.Integrations;

/// <summary>Outbox publisher settings (configuration section "Outbox"); never stored in the database.</summary>
public sealed class OutboxPublisherOptions
{
    /// <summary>Messages claimed per batch: the table is never read whole.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>How often the outbox is polled when it was empty.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a claimed message is reserved for this publisher; after a crash it becomes claimable again.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest wait for the broker's confirmation of one message.</summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>After this many failed attempts a message is dead-lettered (FailedAtUtc) and no longer retried.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>The delay after the first failure; it doubles per attempt up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The delay before attempt <paramref name="attempt"/> + 1, after <paramref name="attempt"/> failures.</summary>
    public TimeSpan RetryDelay(int attempt)
    {
        var factor = Math.Pow(2, Math.Clamp(attempt - 1, 0, 30));
        var delay = TimeSpan.FromTicks((long)Math.Min(BaseRetryDelay.Ticks * factor, MaxRetryDelay.Ticks));
        return delay < MaxRetryDelay ? delay : MaxRetryDelay;
    }
}
