using Grundstuecksfinder.Data;
using Grundstuecksfinder.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Grundstuecksfinder.Infrastructure;

/// <summary>
/// Degraded while a source's latest finished import failed, so a state that keeps failing
/// (unreachable server, incomplete data, regression guard) shows up on /health instead of only
/// in the logs. Degraded still answers 200, so container health checks don't restart the app.
/// </summary>
/// <remarks>
/// <para>
/// Served data with skipped parts counts too: it is complete enough to serve, but the hole would
/// otherwise be invisible here, and nothing fills it before the source's next re-import — which
/// is always the whole state, never the missing tiles alone.
/// </para>
/// <para>
/// A failing probe alone doesn't count while the source still serves data: the data is no worse
/// for it, and a probe that keeps failing surfaces through the home page's "Stand" instead. A
/// source without any data whose probe fails, though, can't even start its first import.
/// </para>
/// </remarks>
public class ImportHealthCheck(IServiceScopeFactory scopeFactory, DisabledSources disabledSources) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // A run still going (or cut short by a crash) has no say; the finished one before it has.
        var failed = await db.ImportRuns
            .Where(r => r.FailedAt != null
                        && !disabledSources.Names.Contains(r.Source)
                        && !db.ImportRuns.Any(newer => newer.Source == r.Source && newer.Id > r.Id
                                                       && (newer.CompletedAt != null || newer.FailedAt != null)))
            .OrderBy(r => r.Source)
            .Select(r => new { r.Source, r.Error })
            .ToListAsync(cancellationToken);

        var states = await db.SourceStates
            .Where(s => !disabledSources.Names.Contains(s.Source))
            .OrderBy(s => s.Source)
            .Select(s => new { s.Source, Served = s.ServedRunId != null, SkippedParts = s.ServedRun == null ? 0 : s.ServedRun.SkippedParts, s.LastProbeError })
            .ToListAsync(cancellationToken);

        var problems = new List<string>();
        if (failed.Count > 0)
            problems.Add("Latest import failed for: " + string.Join("; ", failed.Select(f => $"{f.Source}: {f.Error}")));

        var incomplete = states.Where(s => s.SkippedParts > 0).ToList();
        if (incomplete.Count > 0)
            problems.Add("Served data has skipped parts for: " + string.Join("; ", incomplete.Select(i => $"{i.Source}: {i.SkippedParts}")));

        var unprobed = states.Where(s => !s.Served && s.LastProbeError is not null).ToList();
        if (unprobed.Count > 0)
            problems.Add("No data and probe failing for: " + string.Join("; ", unprobed.Select(u => $"{u.Source}: {u.LastProbeError}")));

        return problems.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded(string.Join(". ", problems));
    }
}
