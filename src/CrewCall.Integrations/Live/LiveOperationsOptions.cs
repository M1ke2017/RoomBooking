namespace CrewCall.Integrations.Live;

/// <summary>The live-operations consumer's configuration ("LiveOperations" section). No hosts here: the broker comes from Aspire.</summary>
public sealed class LiveOperationsOptions
{
    /// <summary>The consumer's durable queue, bound to crewcall.events with the supported routing keys.</summary>
    public string QueueName { get; set; } = "crewcall.live-operations";

    /// <summary>The consumer's inbox identity: a message is broadcast once per consumer name, whatever the deliveries.</summary>
    public string ConsumerName { get; set; } = "crewcall-live-operations";

    /// <summary>How many unacknowledged messages the broker hands this consumer at a time.</summary>
    public ushort PrefetchCount { get; set; } = 20;

    /// <summary>The pause before reconnecting after the broker connection or channel was lost.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The pause before a failed message (e.g. a SignalR or database failure) is requeued for redelivery.</summary>
    public TimeSpan RequeueDelay { get; set; } = TimeSpan.FromSeconds(1);
}
