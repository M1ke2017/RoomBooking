namespace CrewCall.Reporting;

/// <summary>The reporting service's configuration ("Reporting" section). No hosts here: the broker and database come from Aspire.</summary>
public sealed class ReportingOptions
{
    /// <summary>The durable queue, bound to crewcall.events with exactly the routing keys the projections use.</summary>
    public string QueueName { get; set; } = "crewcall.reporting";

    /// <summary>The inbox identity: a message is projected once per consumer name, whatever the deliveries.</summary>
    public string ConsumerName { get; set; } = "crewcall-reporting";

    public ushort PrefetchCount { get; set; } = 20;

    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan RequeueDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest period a technician report may cover, in days (inclusive range).</summary>
    public int MaxReportDays { get; set; } = 366;

    public int MaxTechniciansPerSummary { get; set; } = 100;

    /// <summary>The most visits one visit report returns; the response says when it was truncated.</summary>
    public int MaxVisitRows { get; set; } = 500;
}
