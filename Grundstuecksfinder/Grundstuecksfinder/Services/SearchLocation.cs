namespace Grundstuecksfinder.Services;

/// <summary>A place to search in: either a PLZ or a Gemeinde, picked from one combined list.</summary>
public sealed record SearchLocation(string Name, bool IsPlz)
{
    public string? Plz => IsPlz ? Name : null;
    public string? Gemeinde => IsPlz ? null : Name;

    /// <summary>
    /// PLZ first, then Gemeinden, each in the order given. A typed filter never matches both
    /// kinds at once (PLZ are digits, Gemeinden start with a letter), so the split only shows
    /// in the unfiltered list.
    /// </summary>
    public static IReadOnlyList<SearchLocation> Combine(IEnumerable<string> plz, IEnumerable<string> gemeinden) =>
        [.. plz.Select(p => new SearchLocation(p, IsPlz: true)), .. gemeinden.Select(g => new SearchLocation(g, IsPlz: false))];
}
