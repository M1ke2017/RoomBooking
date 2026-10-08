using CrewCall.Contracts.Integration;
using CrewCall.Persistence.Messaging;

namespace CrewCall.Integrations.Consumers;

/// <summary>
/// The integration-audit consumer's effect (ADR-0014): one technical receipt per message, recorded together with the
/// inbox entry in one transaction, so a redelivered message is recognized and skipped.
/// </summary>
public sealed class IntegrationAuditHandler(IServiceScopeFactory scopes, TimeProvider clock)
{
    public async Task<InboxResult> HandleAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var inbox = scope.ServiceProvider.GetRequiredService<InboxStore>();
        var now = clock.GetUtcNow();
        return await inbox.ProcessOnceAsync(envelope, now, db => InboxStore.AddReceipt(db, envelope, now), cancellationToken);
    }
}
