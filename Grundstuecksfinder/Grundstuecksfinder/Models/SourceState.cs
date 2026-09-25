namespace Grundstuecksfinder.Models;

/// <summary>
/// One row per source: which run's rows are being served, and what the latest probe found.
/// What the scheduler decides on is here or on the served run, so it never has to search the
/// import history for "the latest completed import".
/// </summary>
public class SourceState
{
    /// <summary>The source's stable slug, e.g. "bw".</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// The run whose rows are in Properties; null until the source's first import completes.
    /// Only ever changed by the swap that puts that run's rows in place, in the same transaction.
    /// </summary>
    public int? ServedRunId { get; set; }
    public ImportRun? ServedRun { get; set; }

    /// <summary>When the source was last probed, successfully or not.</summary>
    public DateTimeOffset? LastProbeAt { get; set; }

    /// <summary>The fingerprint the latest successful probe reported.</summary>
    public string? LastProbeFingerprint { get; set; }

    /// <summary>Why the latest probe failed; null once a probe succeeds again.</summary>
    public string? LastProbeError { get; set; }

    /// <summary>
    /// Latest time the served data was confirmed to be within its refresh policy: when it was
    /// imported, and again by every later run that found it not due. The home page shows the
    /// oldest of these across sources as the data's "Stand".
    /// </summary>
    public DateTimeOffset? LastCheckedAt { get; set; }
}
