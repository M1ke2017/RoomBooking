using Daylo.Web.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Daylo.Web;

/// <summary>/health is Healthy only when the database answers.</summary>
public sealed class DatabaseHealthCheck(DayloDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("The database does not answer.");
}
