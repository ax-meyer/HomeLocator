using Grundstuecksfinder.Models;

namespace Grundstuecksfinder.Services.Importers.Scheduling;

/// <summary>Everything the planner knows about one source in this run.</summary>
/// <param name="Source">The source's <see cref="IPropertySource.Id"/>.</param>
/// <param name="Policy">The source's effective refresh policy.</param>
/// <param name="Probe">This run's probe result; null if the probe failed.</param>
/// <param name="Served">The import whose rows are served; null if there is none yet.</param>
/// <param name="LastAttemptFailed">Whether the source's most recent finished import failed.</param>
public sealed record SourceStatus(
    string Source, RefreshPolicy Policy, SourceProbe? Probe, ServedImport? Served, bool LastAttemptFailed = false);

/// <summary>The import a source's rows came from.</summary>
/// <param name="Fingerprint">The upstream version it imported.</param>
/// <param name="StartedAt">
/// When its fetch started. The data is as old as that, not as its completion: a multi-hour
/// import measured from its end would reach MinAge/MaxAge a night late, every time.
/// </param>
public sealed record ServedImport(string Fingerprint, DateTimeOffset StartedAt);

/// <summary>One import the plan asks for, and the probe its fetch is to import.</summary>
public sealed record PlannedImport(string Source, SourceProbe Probe, ImportReason Reason);

/// <summary>The outcome of <see cref="RefreshPlanner.Plan"/>.</summary>
/// <param name="Imports">What to import this run, in order.</param>
/// <param name="Deferred">Due, but beyond this run's routine cap; due again next run.</param>
/// <param name="UpToDate">Sources whose served data is still within their policy.</param>
/// <param name="Unprobed">Sources skipped because their probe failed.</param>
public sealed record RefreshPlan(
    IReadOnlyList<PlannedImport> Imports,
    IReadOnlyList<PlannedImport> Deferred,
    IReadOnlyList<string> UpToDate,
    IReadOnlyList<string> Unprobed);

/// <summary>
/// Decides which sources to import in a run. Pure — all state comes in as
/// <see cref="SourceStatus"/> — so every rule is unit-testable without a database or a clock.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A source without served data is always imported, all of them back to back in the same
/// run: after the database is dropped, the first start imports everything at once rather than
/// one state per night. Those whose last attempt failed go last, so a source that brings the
/// process down (say, out of memory) can't keep every other state from its first import.</item>
/// <item>Otherwise an <see cref="FingerprintKind.Exact"/> source is due iff its fingerprint
/// changed; an <see cref="FingerprintKind.Approximate"/> one iff it changed and the data is at
/// least <see cref="RefreshPolicy.MinAge"/> old, or the data reached
/// <see cref="RefreshPolicy.MaxAge"/>.</item>
/// <item>Of those routine re-imports at most <c>maxRoutineImportsPerRun</c> run, most overdue
/// first, so the multi-hour states are spread over several nights. A source whose last attempt
/// failed goes after the others, so one that keeps failing can't take every night's slot.</item>
/// <item>A source whose probe failed is skipped: nothing is known about it this run.</item>
/// </list>
/// A failed import needs no bookkeeping here: its source's served data is unchanged, so it is
/// simply due again in the next run.
/// </remarks>
public static class RefreshPlanner
{
    public static RefreshPlan Plan(IReadOnlyList<SourceStatus> sources, DateTimeOffset now, int maxRoutineImportsPerRun)
    {
        var initial = new List<(PlannedImport Import, bool Failed)>();
        var routine = new List<(PlannedImport Import, bool Failed, TimeSpan Overdue)>();
        var upToDate = new List<string>();
        var unprobed = new List<string>();

        foreach (var status in sources)
        {
            if (status.Probe is not { } probe)
            {
                unprobed.Add(status.Source);
                continue;
            }
            if (status.Served is not { } served)
            {
                initial.Add((new PlannedImport(status.Source, probe, ImportReason.Initial), status.LastAttemptFailed));
                continue;
            }

            if (Overdue(probe, served, status.Policy, now) is not { } due)
            {
                upToDate.Add(status.Source);
                continue;
            }
            var reason = status.LastAttemptFailed ? ImportReason.Retry : due.Reason;
            routine.Add((new PlannedImport(status.Source, probe, reason), status.LastAttemptFailed, due.Overdue));
        }

        var ordered = routine
            .OrderBy(r => r.Failed)
            .ThenByDescending(r => r.Overdue)
            .ThenBy(r => r.Import.Source, StringComparer.Ordinal)
            .Select(r => r.Import)
            .ToList();
        var cap = Math.Clamp(maxRoutineImportsPerRun, 0, ordered.Count);

        // OrderBy is stable: otherwise the sources keep their registration order.
        var initialOrdered = initial.OrderBy(i => i.Failed).Select(i => i.Import);

        return new RefreshPlan([.. initialOrdered, .. ordered.Take(cap)], ordered.Skip(cap).ToList(), upToDate, unprobed);
    }

    /// <summary>
    /// How long the source has been due and why, or null while its served data is current.
    /// An Exact change counts as due since the served import started: when the new version was
    /// published is unknown, only that it came after the version being served.
    /// </summary>
    private static (ImportReason Reason, TimeSpan Overdue)? Overdue(
        SourceProbe probe, ServedImport served, RefreshPolicy policy, DateTimeOffset now)
    {
        var age = now - served.StartedAt;
        var changed = !string.Equals(probe.Fingerprint, served.Fingerprint, StringComparison.Ordinal);

        if (probe.Kind == FingerprintKind.Exact)
            return changed ? (ImportReason.Changed, age) : null;

        // Both can hold; MinAge <= MaxAge, so a change has then been due for longer.
        if (changed && age >= policy.MinAge)
            return (ImportReason.Changed, age - policy.MinAge);
        if (age >= policy.MaxAge)
            return (ImportReason.MaxAge, age - policy.MaxAge);
        return null;
    }
}
