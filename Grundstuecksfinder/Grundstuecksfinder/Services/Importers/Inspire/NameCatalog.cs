namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// Correct spellings for a source's name components, keyed by the gml:id the source uses for
/// them, taken from another dataset published by the same authority.
/// </summary>
/// <remarks>
/// Hessen publishes its addresses with every non-ASCII character replaced by U+FFFD
/// ("Fuldabr?ck", "Elisabethenstra?e"): the letter itself is gone, so it cannot be recovered
/// from the address data alone. The same authority publishes those names intact in other
/// datasets whose keys line up, so the spelling is looked up rather than guessed.
/// </remarks>
public sealed class NameCatalog(IReadOnlyDictionary<string, string> namesByComponentId)
{
    /// <summary>The character a lossy export leaves where a non-ASCII one used to be.</summary>
    public const char Lost = '�';

    public int Count => namesByComponentId.Count;

    /// <summary>Names handed out by <see cref="Repair"/>, for the import log.</summary>
    public int Repaired { get; private set; }

    /// <summary>Names that were damaged but could not be repaired, for the import log.</summary>
    public int Unrepairable { get; private set; }

    /// <summary>
    /// The catalogue's spelling for a damaged name, or null to keep what the source published.
    /// </summary>
    /// <remarks>
    /// A candidate is only accepted when damaging it the same way reproduces the published text
    /// exactly. The two datasets are separate snapshots, so a key is occasionally reused for a
    /// different street; that check keeps such a name as published instead of replacing it with
    /// the wrong one.
    /// </remarks>
    public string? Repair(string componentId, string published)
    {
        if (!published.Contains(Lost, StringComparison.Ordinal)) return null;

        if (namesByComponentId.TryGetValue(componentId, out var candidate) && Damage(candidate) == published)
        {
            Repaired++;
            return candidate;
        }

        Unrepairable++;
        return null;
    }

    /// <summary>Replaces every non-ASCII character the way the lossy export did.</summary>
    public static string Damage(string name) =>
        string.Create(name.Length, name, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = char.IsAscii(source[i]) ? source[i] : Lost;
        });
}
