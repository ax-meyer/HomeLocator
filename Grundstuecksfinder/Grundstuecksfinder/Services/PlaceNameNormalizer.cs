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
    /// "Cottbus [Chóśebuz]" → "Cottbus" (BB). Returns null for blank input.
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

    /// <summary>
    /// Hessen's WFS replaces every non-ASCII character with U+FFFD server-side, so the original
    /// letter is lost. "Stra\uFFFDe" is the one unambiguous case (and the vast majority of them);
    /// other occurrences are left as-is.
    /// </summary>
    public static string? RepairStreet(string? street)
    {
        if (string.IsNullOrWhiteSpace(street)) return null;
        var trimmed = street.Trim();
        return trimmed.Contains('\uFFFD') ? CorruptedStrasse().Replace(trimmed, "${s}traße") : trimmed;
    }

    [GeneratedRegex(@"\s*\[[^\]]*\]\s*$")]
    private static partial Regex BilingualSuffix();

    [GeneratedRegex(@",\s*(Stadt|Landeshauptstadt|Hansestadt)$")]
    private static partial Regex TitleSuffix();

    [GeneratedRegex(@"^(Stadt|Gemeinde)\s+")]
    private static partial Regex TitlePrefix();

    [GeneratedRegex("(?<s>[Ss])tra\uFFFDe")]
    private static partial Regex CorruptedStrasse();
}
