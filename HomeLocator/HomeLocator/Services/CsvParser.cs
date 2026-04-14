using System.Globalization;
using HomeLocator.Models;

namespace HomeLocator.Services;

public static class CsvParser
{
    // CSV columns (0-indexed): id=0, str=4, hnr=5, hnr_zus=6, plz=7, ort=8, gemeinde=9, flaeche_amtl=16
    public static Property? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var parts = line.Split(';');
        if (parts.Length < 17) return null;

        var str = NullIfEmpty(parts[4]);
        var hnr = NullIfEmpty(parts[5]);

        // Only keep entries with a full street address (Straße + Hausnummer)
        if (str == null || hnr == null) return null;

        return new Property
        {
            Str = str,
            Hnr = hnr,
            HnrZus = NullIfEmpty(parts[6]),
            Plz = NullIfEmpty(parts[7]),
            Ort = NullIfEmpty(parts[8]),
            Gemeinde = NullIfEmpty(parts[9]),
            FlaecheAmtl = double.TryParse(parts[16], NumberStyles.Any, CultureInfo.InvariantCulture, out var f) ? f : null,
        };
    }

    private static string? NullIfEmpty(string s) =>
        string.IsNullOrWhiteSpace(s) || s.Equals("NULL", StringComparison.OrdinalIgnoreCase) ? null : s.Trim();
}
