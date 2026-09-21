namespace Grundstuecksfinder.Services;

/// <summary>
/// Sources switched off via config ("Enabled": false). Their rows stay in the database, so
/// switching a source back on needs only a restart, not a re-import — but search, the filter
/// dropdowns and the counts ignore them.
/// </summary>
public sealed record DisabledSources(IReadOnlyList<string> Names)
{
    public static DisabledSources None { get; } = new([]);
}
