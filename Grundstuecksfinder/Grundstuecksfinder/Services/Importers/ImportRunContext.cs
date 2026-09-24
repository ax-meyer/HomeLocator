namespace Grundstuecksfinder.Services.Importers;

/// <summary>
/// The import run a fetch belongs to, and the channel for what the fetch has to report besides
/// its rows. <see cref="PropertyBulkWriter"/> records it on the run in the same transaction
/// that swaps the rows in, so a served import and what is known about its gaps never disagree.
/// </summary>
public sealed class ImportRunContext(int runId, string source)
{
    /// <summary>The ImportRuns row this fetch's rows are recorded against.</summary>
    public int RunId { get; } = runId;

    /// <summary>The source's stable slug; the discriminator its rows are written with.</summary>
    public string Source { get; } = source;

    /// <summary>Parts of the source the fetch gave up on and imported without; see <see cref="AddSkippedParts"/>.</summary>
    public int SkippedParts { get; private set; }

    /// <summary>
    /// Reports parts of the source (INSPIRE tiles) the fetch tolerated missing rather than
    /// failing the whole import, so the hole shows on /health instead of only in the logs.
    /// Report before the stream ends: the writer reads this once the last row has been staged.
    /// </summary>
    public void AddSkippedParts(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        SkippedParts += count;
    }

    /// <summary>
    /// The fingerprint of the data the fetch actually imported, where it differs from the probe's;
    /// null means the probe's. See <see cref="ReportFingerprint"/>.
    /// </summary>
    public string? ImportedFingerprint { get; private set; }

    /// <summary>
    /// Reports what the fetch really imported, for a source whose upstream can move between the
    /// probe and the fetch (a file located again at download time may be a newer edition). The
    /// run is recorded with this fingerprint instead of the probe's, so the next run compares
    /// against the data actually served and doesn't import that edition a second time.
    /// </summary>
    public void ReportFingerprint(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ImportedFingerprint = fingerprint;
    }
}
