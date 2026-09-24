namespace Grundstuecksfinder.Services.Importers.Scheduling;

/// <summary>
/// How stale a source's served data may get, for sources whose fingerprint is only
/// <see cref="FingerprintKind.Approximate"/>: a changed fingerprint makes the source due once
/// its data is at least <see cref="MinAge"/> old, and it is due at <see cref="MaxAge"/> even
/// when the fingerprint looks unchanged (renamings, corrected areas and equal adds and removes
/// don't move hit counts). <see cref="FingerprintKind.Exact"/> sources ignore both: a new
/// version is imported as soon as it is published, and an unchanged one never.
/// </summary>
public sealed record RefreshPolicy(TimeSpan MinAge, TimeSpan MaxAge)
{
    /// <summary>
    /// Quarterly is fresh enough for cadastral data; a month keeps daily hit-count drift from
    /// re-importing a large state (BW takes hours) every night.
    /// </summary>
    public static RefreshPolicy Default { get; } = new(TimeSpan.FromDays(30), TimeSpan.FromDays(90));
}
