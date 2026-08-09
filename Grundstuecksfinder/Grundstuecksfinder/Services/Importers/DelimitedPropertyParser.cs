using System.Globalization;
using Grundstuecksfinder.Models;

namespace Grundstuecksfinder.Services.Importers;

/// <summary>Parses one line of a delimited text source into a Property, driven by a <see cref="CsvColumnMap"/>.</summary>
public static class DelimitedPropertyParser
{
    public static Property? ParseLine(string line, CsvColumnMap map)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var parts = line.Split(map.Delimiter);
        if (parts.Length < map.MinColumnCount) return null;

        var str = NullIfEmpty(parts[map.StrIndex], map.NullToken);
        var hnr = NullIfEmpty(parts[map.HnrIndex], map.NullToken);

        // Only keep entries with a full street address (Straße + Hausnummer)
        if (str == null || hnr == null) return null;

        return new Property
        {
            Str = str,
            Hnr = hnr,
            HnrZus = NullIfEmpty(parts[map.HnrZusIndex], map.NullToken),
            Plz = NullIfEmpty(parts[map.PlzIndex], map.NullToken),
            Ort = NullIfEmpty(parts[map.OrtIndex], map.NullToken),
            Gemeinde = NullIfEmpty(parts[map.GemeindeIndex], map.NullToken),
            FlaecheAmtl = double.TryParse(parts[map.FlaecheAmtlIndex], NumberStyles.Any, CultureInfo.InvariantCulture, out var f) ? f : null,
        };
    }

    private static string? NullIfEmpty(string s, string nullToken) =>
        string.IsNullOrWhiteSpace(s) || s.Equals(nullToken, StringComparison.OrdinalIgnoreCase) ? null : s.Trim();
}
