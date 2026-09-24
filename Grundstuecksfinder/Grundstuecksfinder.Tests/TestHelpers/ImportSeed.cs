using Grundstuecksfinder.Models;

namespace Grundstuecksfinder.Tests.TestHelpers;

/// <summary>
/// Import history rows for tests that need a source served (or failed) without running an
/// import. Times are arbitrary but UTC, as Postgres' timestamptz requires.
/// </summary>
public static class ImportSeed
{
    public static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary><see cref="Epoch"/> plus <paramref name="days"/> days.</summary>
    public static DateTimeOffset Day(double days) => Epoch.AddDays(days);

    public static ImportRun Completed(string source, DateTimeOffset completedAt, long recordCount = 1,
        string fingerprint = "v1", int skippedParts = 0) => new()
    {
        Source = source, Fingerprint = fingerprint, Reason = ImportReason.Initial,
        StartedAt = completedAt, CompletedAt = completedAt, RecordCount = recordCount, SkippedParts = skippedParts,
    };

    public static ImportRun Failed(string source, DateTimeOffset failedAt, string error, string fingerprint = "v2") => new()
    {
        Source = source, Fingerprint = fingerprint, Reason = ImportReason.Changed,
        StartedAt = failedAt, FailedAt = failedAt, Error = error,
    };

    /// <summary>Still going, or cut short by a crash.</summary>
    public static ImportRun Unfinished(string source, DateTimeOffset startedAt, string fingerprint = "v3") => new()
    {
        Source = source, Fingerprint = fingerprint, Reason = ImportReason.Changed, StartedAt = startedAt,
    };

    /// <summary>The source's state with <paramref name="run"/> served, confirmed at its completion unless given.</summary>
    public static SourceState Serving(ImportRun run, DateTimeOffset? lastCheckedAt = null) => new()
    {
        Source = run.Source, ServedRun = run, LastCheckedAt = lastCheckedAt ?? run.CompletedAt,
    };
}
