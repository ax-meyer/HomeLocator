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
/// <remarks>
/// An import that succeeded with tiles skipped counts too: it is complete enough to serve, but
/// the hole would otherwise be invisible here, and nothing retries it before the source's
/// version changes — a re-import is always the whole state, never the missing tiles alone.
/// </remarks>
public class ImportHealthCheck(IServiceScopeFactory scopeFactory, DisabledSources disabledSources) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var latest = await db.ImportLogs
            .Where(l => (l.LastError != null || l.SkippedTiles > 0)
                        && !disabledSources.Names.Contains(l.Source)
                        && !db.ImportLogs.Any(newer => newer.Source == l.Source && newer.ImportedAt > l.ImportedAt))
            .Select(l => new { l.Source, l.LastError, l.SkippedTiles })
            .ToListAsync(cancellationToken);

        var problems = new List<string>();
        var failed = latest.Where(l => l.LastError != null).ToList();
        if (failed.Count > 0)
            problems.Add("Latest import failed for: " + string.Join("; ", failed.Select(f => $"{f.Source}: {f.LastError}")));

        // A failed import reports nothing about tiles: its rows never reached the table.
        var incomplete = latest.Where(l => l.LastError is null && l.SkippedTiles > 0).ToList();
        if (incomplete.Count > 0)
            problems.Add("Latest import skipped tiles for: " + string.Join("; ", incomplete.Select(i => $"{i.Source}: {i.SkippedTiles}")));

        return problems.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded(string.Join(". ", problems));
    }
}
