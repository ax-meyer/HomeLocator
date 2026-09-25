namespace Grundstuecksfinder.Services.Importers;

/// <summary>
/// What a source's upstream offers right now, as reported by a cheap probe (a HEAD request, a
/// manifest, WFS hit counts): a fingerprint to compare with the one of the data being served,
/// and how far that comparison can be trusted. Carries no time component — how often a source
/// is refreshed is the planner's decision, not the fingerprint's.
/// </summary>
/// <remarks>
/// Not sealed on purpose: a source may derive from it to hand what its fetch needs (say, the
/// file name the probe found) to its own fetch, so the fetch imports exactly what was probed.
/// </remarks>
public record SourceProbe(string Fingerprint, FingerprintKind Kind);

/// <summary>How much a changed <see cref="SourceProbe.Fingerprint"/> says about the data.</summary>
public enum FingerprintKind
{
    /// <summary>
    /// The publisher's own version marker (ETag, Last-Modified, a manifest timestamp, an edition
    /// date): it changes exactly when new data is published, so a source is imported iff its
    /// fingerprint differs from the served one — and an unchanged file is never downloaded again.
    /// </summary>
    Exact,

    /// <summary>
    /// Derived from a live service (WFS hit counts): a change only means that something moved,
    /// and active states' counts drift daily, so a change alone doesn't make the source due —
    /// see <see cref="Scheduling.RefreshPolicy"/>. A composite source is Approximate as soon as
    /// one of its parts is.
    /// </summary>
    Approximate,
}
