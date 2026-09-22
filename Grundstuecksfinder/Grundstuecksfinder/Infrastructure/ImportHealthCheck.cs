using Grundstuecksfinder.Data;
using Grundstuecksfinder.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Grundstuecksfinder.Infrastructure;

/// <summary>
/// Degraded while a source's latest import attempt failed, so a state that keeps failing
/// (unreachable server, incomplete data, regression guard) shows up on /health instead of only
/// in the logs. Degraded still answers 200, so container health checks don't restart the app.
/// </summary>
public class ImportHealthCheck(IServiceScopeFactory scopeFactory, DisabledSources disabledSources) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var failing = await db.ImportLogs
            .Where(l => l.LastError != null
                        && !disabledSources.Names.Contains(l.Source)
                        && !db.ImportLogs.Any(newer => newer.Source == l.Source && newer.ImportedAt > l.ImportedAt))
            .Select(l => new { l.Source, l.LastError })
            .ToListAsync(cancellationToken);

        return failing.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded(
                "Latest import failed for: " + string.Join("; ", failing.Select(f => $"{f.Source}: {f.LastError}")));
    }
}
