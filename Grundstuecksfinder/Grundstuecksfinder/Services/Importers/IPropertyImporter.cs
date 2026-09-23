using Grundstuecksfinder.Models;

namespace Grundstuecksfinder.Services.Importers;

/// <summary>
/// A single region/country's data source. Register an implementation in DI to wire it up —
/// the <see cref="ImportOrchestrator"/> and DB schema need no changes.
/// </summary>
public interface IPropertyImporter
{
    /// <summary>
    /// Stable slug for this source, e.g. "nrw". Used as the discriminator on Property/ImportLog
    /// rows. Must never change once deployed, or existing rows become orphaned.
    /// </summary>
    string Source { get; }

    /// <summary>Cheap check of what's available upstream, without downloading full payloads.</summary>
    Task<IReadOnlyList<ImportCandidate>> DiscoverAsync(CancellationToken ct);

    /// <summary>Fetches and normalizes one candidate into Property rows, streamed.</summary>
    IAsyncEnumerable<Property> FetchAsync(ImportCandidate candidate, CancellationToken ct);

    /// <summary>
    /// Parts of the source the last <see cref="FetchAsync"/> tolerated missing rather than
    /// failing the import — INSPIRE tiles whose requests kept failing. Read after the stream has
    /// been consumed and recorded on the ImportLog, so a hole shows on /health instead of only
    /// in the logs. Importers that fetch a source in one piece leave it at 0.
    /// </summary>
    int SkippedTiles => 0;
}

/// <summary>An importable unit of upstream data, as reported by <see cref="IPropertyImporter.DiscoverAsync"/>.</summary>
public sealed record ImportCandidate(string DatasetName, string FileName, string VersionTimestamp);
