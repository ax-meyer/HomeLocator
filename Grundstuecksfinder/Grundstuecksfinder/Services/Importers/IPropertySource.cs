using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers.Scheduling;

namespace Grundstuecksfinder.Services.Importers;

/// <summary>
/// A single region/country's data source. Register an implementation in DI to wire it up —
/// the <see cref="ImportRunner"/>, the planner and the DB schema need no changes.
/// </summary>
public interface IPropertySource
{
    /// <summary>
    /// Stable slug for this source, e.g. "nrw". Used as the discriminator on Property, ImportRun
    /// and SourceState rows. Must never change once deployed, or existing rows become orphaned.
    /// </summary>
    string Id { get; }

    /// <summary>How stale the source's data may get before it is re-imported; from config.</summary>
    RefreshPolicy RefreshPolicy { get; }

    /// <summary>
    /// Cheap check of what upstream offers (a HEAD request, a manifest, hit counts), without
    /// downloading the payload. Throws if it can't tell; the source is then skipped this run.
    /// </summary>
    Task<SourceProbe> ProbeAsync(CancellationToken ct);

    /// <summary>
    /// Fetches and normalizes what <paramref name="probe"/> (this source's own probe from the
    /// same run) found into Property rows, streamed. Parts tolerated missing are reported to
    /// <paramref name="run"/> before the stream ends; anything worse must throw, so the previous
    /// rows stay.
    /// </summary>
    IAsyncEnumerable<Property> FetchAsync(SourceProbe probe, ImportRunContext run, CancellationToken ct);
}
