namespace CrewCall.Web.Status;

public enum ComponentState
{
    Healthy,
    Degraded,
    Unavailable,
    Unknown
}

public sealed record ComponentStatus(ComponentState State, string Detail);

public sealed record SystemStatus(ComponentStatus Api, ComponentStatus Database, string ApiService, DateTimeOffset CheckedAt);
