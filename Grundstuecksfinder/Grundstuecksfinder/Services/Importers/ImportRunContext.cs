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
}
