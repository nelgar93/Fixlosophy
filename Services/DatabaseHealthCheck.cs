using Fixlosophy.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Fixlosophy.Services;

/// <summary>
/// Backs <c>/health</c>: reports healthy only if the database actually answers.
/// </summary>
/// <remarks>
/// <para>Hand-written rather than pulling in
/// <c>Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore</c> for its
/// <c>AddDbContextCheck</c>, which does the same <c>CanConnectAsync</c> call. A whole
/// package for fifteen lines isn't worth the dependency, and this way what the probe
/// does is visible here rather than behind an extension method.</para>
///
/// <para>Deliberately just "can I reach it" — not a query against a real table, and
/// certainly not a write. A health check runs every few seconds forever, so it has to
/// stay cheap enough to be invisible in the connection pool and in Supabase's
/// connection limit.</para>
/// </remarks>
public sealed class DatabaseHealthCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("Database reachable.")
                : HealthCheckResult.Unhealthy("Database did not answer.");
        }
        catch (Exception ex)
        {
            // The exception message goes in the result, not the response body —
            // MapHealthChecks writes only the status text by default, so connection
            // details never reach the caller.
            return HealthCheckResult.Unhealthy("Database did not answer.", ex);
        }
    }
}
