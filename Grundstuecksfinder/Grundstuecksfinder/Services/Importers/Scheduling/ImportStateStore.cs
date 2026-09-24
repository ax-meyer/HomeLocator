using Grundstuecksfinder.Data;
using Grundstuecksfinder.Models;
using Microsoft.EntityFrameworkCore;

namespace Grundstuecksfinder.Services.Importers.Scheduling;

/// <summary>What the history says about a source, as far as planning is concerned.</summary>
public sealed record SourceHistory(ServedImport? Served, bool LastAttemptFailed);

/// <summary>
/// The import runner's reads and writes of ImportRuns and SourceStates, apart from the one
/// <see cref="PropertyBulkWriter"/> makes in its swap transaction. Every write is a single
/// statement and nothing is kept tracked, so a run that lasts hours never works on stale
/// entities while the writer changes the same rows underneath.
/// </summary>
public sealed class ImportStateStore(AppDbContext db)
{
    /// <summary>Longer messages (a whole response body) are cut; /health shows this.</summary>
    public const int MaxErrorLength = 2000;

    /// <summary>The error recorded on a run found unfinished; see <see cref="FailInterruptedRunsAsync"/>.</summary>
    public const string InterruptedError = "interrupted: the process stopped during the import";

    /// <summary>Every source with a history; a source missing here has never been imported.</summary>
    public async Task<IReadOnlyDictionary<string, SourceHistory>> LoadAsync(CancellationToken ct)
    {
        var served = await db.SourceStates
            .Where(s => s.ServedRun != null)
            .Select(s => new { s.Source, s.ServedRun!.Fingerprint, s.ServedRun.StartedAt })
            .ToDictionaryAsync(s => s.Source, s => new ServedImport(s.Fingerprint, s.StartedAt), ct);
        var failed = (await db.LatestRunsIfFailed.Select(r => r.Source).ToListAsync(ct)).ToHashSet();

        return served.Keys.Union(failed).ToDictionary(
            source => source,
            source => new SourceHistory(served.GetValueOrDefault(source), failed.Contains(source)));
    }

    /// <summary>
    /// Records a probe's outcome: its fingerprint, or why it failed. A failed probe keeps the
    /// last known fingerprint.
    /// </summary>
    public async Task RecordProbeAsync(string source, DateTimeOffset at, string? fingerprint, string? error, CancellationToken ct)
    {
        error = Truncate(error);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "SourceStates" ("Source", "LastProbeAt", "LastProbeFingerprint", "LastProbeError")
            VALUES ({source}, {at}, {fingerprint}, {error})
            ON CONFLICT ("Source") DO UPDATE SET
                "LastProbeAt" = excluded."LastProbeAt",
                "LastProbeFingerprint" = COALESCE(excluded."LastProbeFingerprint", "SourceStates"."LastProbeFingerprint"),
                "LastProbeError" = excluded."LastProbeError"
            """, ct);
    }

    /// <summary>The sources' served data was found within its refresh policy at <paramref name="at"/>.</summary>
    public async Task ConfirmCurrentAsync(IReadOnlyCollection<string> sources, DateTimeOffset at, CancellationToken ct)
    {
        if (sources.Count == 0) return;
        await db.SourceStates
            .Where(s => sources.Contains(s.Source) && s.ServedRunId != null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastCheckedAt, at), ct);
    }

    /// <summary>
    /// Records the start of an import with the probe's fingerprint; the ID is what the run's rows
    /// and its completion are recorded against. It stays unfinished until the swap completes it
    /// or <see cref="FailRunAsync"/>.
    /// </summary>
    public async Task<int> StartRunAsync(PlannedImport planned, DateTimeOffset at, CancellationToken ct)
    {
        var run = new ImportRun
        {
            Source = planned.Source,
            Fingerprint = planned.Probe.Fingerprint,
            Reason = planned.Reason,
            StartedAt = at,
        };
        db.ImportRuns.Add(run);
        await db.SaveChangesAsync(ct);
        db.Entry(run).State = EntityState.Detached;
        return run.Id;
    }

    /// <summary>
    /// Records the run as failed, with the fingerprint its fetch reported having got to (see
    /// <see cref="ImportRunContext.ReportFingerprint"/>) if any.
    /// </summary>
    public async Task FailRunAsync(ImportRunContext run, DateTimeOffset at, string error, CancellationToken ct)
    {
        var truncated = Truncate(error);
        var fingerprint = run.ImportedFingerprint;
        await db.ImportRuns
            .Where(r => r.Id == run.RunId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.FailedAt, at)
                .SetProperty(r => r.Error, truncated)
                .SetProperty(r => r.Fingerprint, r => fingerprint ?? r.Fingerprint), ct);
    }

    /// <summary>Removes a run that never got to import anything, so it isn't taken for an attempt.</summary>
    public async Task DiscardRunAsync(int runId, CancellationToken ct) =>
        await db.ImportRuns.Where(r => r.Id == runId).ExecuteDeleteAsync(ct);

    /// <summary>
    /// Records every unfinished run as failed. Only valid while holding the whole-run lock: then
    /// no run can be in progress, so an unfinished one was cut short by a crash or a shutdown.
    /// Failed, it shows on /health and counts as its source's last attempt. Returns how many.
    /// </summary>
    public async Task<int> FailInterruptedRunsAsync(DateTimeOffset at, CancellationToken ct) =>
        await db.ImportRuns
            .Where(r => r.CompletedAt == null && r.FailedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.FailedAt, at).SetProperty(r => r.Error, InterruptedError), ct);

    private static string? Truncate(string? error) =>
        error is { Length: > MaxErrorLength } ? error[..MaxErrorLength] : error;
}
