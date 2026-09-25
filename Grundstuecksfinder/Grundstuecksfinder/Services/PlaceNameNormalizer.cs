using System.Text.RegularExpressions;

namespace Grundstuecksfinder.Services;

/// <summary>
/// Cleans up official ALKIS/INSPIRE place and street names into the plain form people (and
/// Nominatim) use. Applied at import time and again before geocoding, so rows imported before
/// these rules existed still produce working map links.
/// </summary>
public static partial class PlaceNameNormalizer
{
    /// <summary>
    /// Strips title prefixes/suffixes and bilingual additions from a Gemeinde/Ort name:
    /// "Stadt Pirna" → "Pirna" (SN), "Kiel, Landeshauptstadt" → "Kiel" (SH),
    /// "Cottbus [Chóśebuz]" → "Cottbus" (BB), "Adelebsen, Flecken" → "Adelebsen" and
    /// "Juist, Inselgemeinde" → "Juist" (NI). Returns null for blank input.
    /// </summary>
    public static string? NormalizePlace(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var result = BilingualSuffix().Replace(name.Trim(), "");
        result = TitleSuffix().Replace(result, "");
        // Only one prefix: SN's "Stadt Stadt Wehlen" is the town officially named "Stadt Wehlen".
        result = TitlePrefix().Replace(result, "", 1);

        return result.Length == 0 ? null : result;
    }

    /// <summary>A street name trimmed; null for blank input.</summary>
    public static string? NormalizeStreet(string? street) =>
        string.IsNullOrWhiteSpace(street) ? null : street.Trim();

    [GeneratedRegex(@"\s*\[[^\]]*\]\s*$")]
    private static partial Regex BilingualSuffix();

    /// <summary>
    /// A trailing municipal title, matched by the head noun its compounds end in rather than by
    /// a fixed list — every state invents its own: ", Stadt" and ", Landeshauptstadt" (SH, SN),
    /// ", Flecken", ", Klosterflecken", ", Inselgemeinde", ", Klostergemeinde", ", Nordseebad"
    /// and ", Berg- und Universitätsstadt" (NI), ", Kurort" (TH).
    /// </summary>
    [GeneratedRegex(@",\s*[\p{L}\-. ]*?(?:stadt|gemeinde|flecken|bad|kurort)$", RegexOptions.IgnoreCase)]
    private static partial Regex TitleSuffix();

    [GeneratedRegex(@"^(Stadt|Gemeinde)\s+")]
    private static partial Regex TitlePrefix();
}
