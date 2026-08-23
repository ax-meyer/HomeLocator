using System.Globalization;
using Grundstuecksfinder.Models;

namespace Grundstuecksfinder.Services.Importers;

/// <summary>Parses one line of a delimited text source into a Property, driven by a <see cref="CsvColumnMap"/>.</summary>
public static class DelimitedPropertyParser
{
    public static Property? ParseLine(string line, CsvColumnMap map)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        // The NRW export is CSV: fields such as lagebeztext may contain semicolons
        // enclosed in double quotes. String.Split would shift every following column.
        var parts = ParseFields(line, map.Delimiter, map.MinColumnCount);
        if (parts == null) return null;

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

    private static string[]? ParseFields(string line, char delimiter, int requiredFieldCount)
    {
        var fields = new string[requiredFieldCount];
        var fieldIndex = 0;
        var fieldStart = 0;
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    index++; // Escaped quote within a quoted field.
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (line[index] != delimiter || inQuotes) continue;

            if (fieldIndex < requiredFieldCount)
                fields[fieldIndex] = Unquote(line[fieldStart..index]);

            fieldIndex++;
            fieldStart = index + 1;

            // All fields used by this importer are available. Do not parse the
            // potentially large JSON columns that follow them.
            if (fieldIndex == requiredFieldCount)
                return fields;
        }

        if (inQuotes || fieldIndex >= requiredFieldCount) return null;

        fields[fieldIndex] = Unquote(line[fieldStart..]);
        fieldIndex++;
        return fieldIndex >= requiredFieldCount ? fields : null;
    }

    private static string Unquote(string field)
    {
        var value = field.Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1].Replace("\"\"", "\"")
            : value;
    }
}
