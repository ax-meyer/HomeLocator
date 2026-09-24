namespace Grundstuecksfinder.Models;

/// <summary>
/// One attempt at importing a source. Append-only: a retry is a new run, so the history shows
/// every attempt and why it was made. A run is still going while neither
/// <see cref="CompletedAt"/> nor <see cref="FailedAt"/> is set — or was cut short by a crash,
/// which leaves it that way for good; nothing depends on it finishing.
/// </summary>
public class ImportRun
{
    public int Id { get; set; }

    /// <summary>The source's stable slug, e.g. "bw"; the same as on its <see cref="Property"/> rows.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The fingerprint the source's probe reported, i.e. the upstream version this run imports.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public ImportReason Reason { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// When the run's rows were swapped into Properties. Set in the swap's own transaction, so a
    /// completed run always has its data and swapped data is never recorded as failed.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>When the run gave up; its rows never reached Properties and the source's previous rows stayed.</summary>
    public DateTimeOffset? FailedAt { get; set; }

    /// <summary>Why the run failed; reported on /health until a later run of the source succeeds.</summary>
    public string? Error { get; set; }

    /// <summary>Rows swapped in; 0 unless completed.</summary>
    public long RecordCount { get; set; }

    /// <summary>
    /// Parts of the source (INSPIRE tiles) the fetch gave up on and imported without. Non-zero
    /// means the data was complete enough to serve but has a known hole, which the import health
    /// check reports for as long as this run is served — a re-import is always the whole source.
    /// </summary>
    public int SkippedParts { get; set; }
}
