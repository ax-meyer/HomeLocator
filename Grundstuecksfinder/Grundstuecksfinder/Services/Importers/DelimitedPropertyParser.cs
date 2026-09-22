using System.Globalization;
using System.Text.RegularExpressions;
using Grundstuecksfinder.Models;

namespace Grundstuecksfinder.Services.Importers;

/// <summary>Parses one line of a delimited text source into a Property, driven by a <see cref="CsvColumnMap"/>.</summary>
public static class DelimitedPropertyParser
{
    private static readonly Regex PlzPattern = new(@"^\d{5}$", RegexOptions.Compiled);
    private static readonly Regex GemeindePattern = new(@"^\p{L}", RegexOptions.Compiled);

    public static Property? ParseLine(string line, CsvColumnMap map)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var parts = SplitCsvLine(line, map.Delimiter);
        if (parts.Count < map.MinColumnCount) return null;

        var str = NullIfEmpty(parts[map.StrIndex], map.NullToken);
        var hnr = NullIfEmpty(parts[map.HnrIndex], map.NullToken);

        // Only keep entries with a full street address (Straße + Hausnummer)
        if (str == null || hnr == null) return null;

        var plz = NullIfEmpty(parts[map.PlzIndex], map.NullToken);
        var gemeinde = NullIfEmpty(parts[map.GemeindeIndex], map.NullToken);

        // Reject rows with obviously invalid PLZ or Gemeinde to keep dropdowns clean
        if (plz != null && !PlzPattern.IsMatch(plz)) plz = null;
        if (gemeinde != null && !GemeindePattern.IsMatch(gemeinde)) gemeinde = null;

        return new Property
        {
            Str = str,
            Hnr = hnr,
            HnrZus = NullIfEmpty(parts[map.HnrZusIndex], map.NullToken),
            Plz = plz,
            Ort = NullIfEmpty(parts[map.OrtIndex], map.NullToken),
            Gemeinde = gemeinde,
            FlaecheAmtl = double.TryParse(parts[map.FlaecheAmtlIndex], NumberStyles.Any, CultureInfo.InvariantCulture, out var f) ? f : null,
        };
    }

    /// <summary>
    /// Splits a CSV/delimited line respecting RFC 4180 double-quote escaping.
    /// Fields wrapped in quotes can contain the delimiter and literal quotes (escaped as "").
    /// </summary>
    public static List<string> SplitCsvLine(ReadOnlySpan<char> line, char delimiter)
    {
        var fields = new List<string>();
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] == '"')
            {
                // Quoted field — read until closing quote
                i++; // skip opening quote
                var start = i;
                var sb = new System.Text.StringBuilder();
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            // Escaped quote ""
                            sb.Append(line[start..i]);
                            sb.Append('"');
                            i += 2;
                            start = i;
                        }
                        else
                        {
                            // Closing quote
                            sb.Append(line[start..i]);
                            i++; // skip closing quote
                            break;
                        }
                    }
                    else
                    {
                        i++;
                    }
                }
                fields.Add(sb.ToString());
                // Skip delimiter after closing quote
                if (i < line.Length && line[i] == delimiter)
                    i++;
            }
            else
            {
                // Unquoted field
                var start = i;
                while (i < line.Length && line[i] != delimiter)
                    i++;
                fields.Add(line[start..i].ToString());
                if (i < line.Length)
                {
                    i++; // skip delimiter
                    // Trailing delimiter means there's an empty field after it
                    if (i == line.Length)
                        fields.Add("");
                }
            }
        }
        return fields;
    }

    private static string? NullIfEmpty(string s, string nullToken) =>
        string.IsNullOrWhiteSpace(s) || s.Equals(nullToken, StringComparison.OrdinalIgnoreCase) ? null : s.Trim();
}
