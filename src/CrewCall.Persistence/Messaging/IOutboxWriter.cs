using CrewCall.Contracts.Integration;

namespace CrewCall.Persistence.Messaging;

/// <summary>
/// Adds an integration event to the current unit of work as an outbox message. Never publishes: the message is written by
/// the caller's SaveChanges, in the same transaction as the business change, or not at all (ADR-0014).
/// </summary>
public interface IOutboxWriter
{
    void Add(IIntegrationEvent integrationEvent, string? aggregateType = null, Guid? aggregateId = null);
}
