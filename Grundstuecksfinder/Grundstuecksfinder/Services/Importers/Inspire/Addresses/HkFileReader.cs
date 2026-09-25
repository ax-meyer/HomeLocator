using System.Globalization;
using System.Text.RegularExpressions;

namespace Grundstuecksfinder.Services.Importers.Inspire.Addresses;

/// <summary>What <see cref="HkFileReader.Read"/> found.</summary>
/// <param name="Rows">Data lines read (the header and blank lines not counted).</param>
/// <param name="Accepted">Rows of an allowed quality, added to the builder.</param>
/// <param name="OtherQuality">Rows dropped for their quality ("qua") alone.</param>
/// <param name="Malformed">Rows that couldn't be read: another field count than the header's, unreadable coordinates.</param>
public sealed record HkReadResult(long Rows, long Accepted, long OtherQuality, long Malformed);

/// <summary>
/// Reads a "Hauskoordinaten" (HK) text export — the ZSHH standard format every state's survey
/// office publishes its georeferenced addresses in — into a <see cref="PreloadedAddresses.Builder"/>.
/// </summary>
/// <remarks>
/// <para>
/// The format is only loosely standard, so everything is taken from the file itself. Columns
/// are found by their header name, never by position: Hessen's export lacks the postal columns
/// altogether (and spells "kreisschl" as "kreissch"), Baden-Württemberg's has them but empty.
/// The delimiter is whichever of ';', ',', tab and '|' the header uses most (BW ';', HE ',').
/// Fields may be quoted, with "" for a quote inside (HE quotes its numeric fields). Lines may
/// end in CRLF or LF. A quoted field spanning lines is not supported; neither file has one.
/// </para>
/// <para>
/// The mapping onto an address: Str = str, Hnr = hnr, HnrZus = adz, Plz = postplz if the column
/// exists and holds a PLZ, Ort = ott (the Ortsteil) if given, else gmd, Gemeinde = gmd — place
/// names run through <see cref="PlaceNameNormalizer"/> like every other source's. An ott that
/// only numbers a city district ("Frankfurt Bezirk 32") counts as not given: it is no place
/// name anyone uses, and Nominatim knows none of them.
/// </para>
/// <para>
/// ~3.4 M rows for BW: each row's fields are only looked at as spans, and every text value is
/// resolved through a cache keyed by those spans, so a repeated value (which almost all are)
/// costs no allocation.
/// </para>
/// </remarks>
public static partial class HkFileReader
{
    private static readonly char[] CandidateDelimiters = [';', ',', '\t', '|'];

    /// <param name="reader">The file's text, header first.</param>
    /// <param name="allowedQualities">"qua" values to keep (the others are counted and dropped).</param>
    /// <param name="utmZone">
    /// The UTM zone of the source's CRS. A row whose "zone" says otherwise fails the read: its
    /// coordinates would silently land kilometres off, like a WFS answer in a foreign CRS.
    /// </param>
    /// <param name="into">Receives every accepted row.</param>
    /// <param name="ct">Checked every few thousand rows.</param>
    public static HkReadResult Read(
        TextReader reader, IReadOnlyCollection<string> allowedQualities, int utmZone,
        PreloadedAddresses.Builder into, CancellationToken ct)
    {
        var header = reader.ReadLine()?.TrimStart('﻿')
                     ?? throw new InspireImportException("The Hauskoordinaten file is empty.");
        var delimiter = DetectDelimiter(header);
        var columns = Columns.FromHeader(header, delimiter);

        var buffer = new char[Math.Max(256, header.Length)];
        var fields = new Range[columns.Count];
        var streets = new SpanCache(value => value);
        var houseNumbers = new SpanCache(value => value);
        var places = new SpanCache(PlaceNameNormalizer.NormalizePlace);
        var ortsteile = new SpanCache(ott => NumberedDistrict().IsMatch(ott) ? null : PlaceNameNormalizer.NormalizePlace(ott));
        var postcodes = new SpanCache(PostalCode.Normalize);

        long rows = 0, accepted = 0, otherQuality = 0, malformed = 0;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            if (++rows % 16384 == 0) ct.ThrowIfCancellationRequested();

            if (buffer.Length < line.Length) buffer = new char[line.Length * 2];
            // A delimiter inside an unquoted value would shift every column after it, so a row
            // must have exactly the header's fields to be read at all.
            if (SplitFields(line, delimiter, buffer, fields) != columns.Count)
            {
                malformed++;
                continue;
            }
            ReadOnlySpan<char> Field(int index) => index < 0 ? default : buffer.AsSpan(fields[index]).Trim();

            if (!IsAllowed(Field(columns.Quality), allowedQualities))
            {
                otherQuality++;
                continue;
            }

            // NumberStyles.Float also reads "NaN" and "Infinity", which no point can be at.
            if (!double.TryParse(Field(columns.East), NumberStyles.Float, CultureInfo.InvariantCulture, out var east) ||
                !double.TryParse(Field(columns.North), NumberStyles.Float, CultureInfo.InvariantCulture, out var north) ||
                !double.IsFinite(east) || !double.IsFinite(north))
            {
                malformed++;
                continue;
            }

            var zone = Field(columns.Zone);
            if (!zone.IsEmpty && (!int.TryParse(zone, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowZone) || rowZone != utmZone))
                throw new InspireImportException(FormattableString.Invariant(
                    $"Row {rows} of the Hauskoordinaten file is in UTM zone \"{zone.ToString()}\", not the source's zone {utmZone}."));

            var gemeinde = places.Get(Field(columns.Gemeinde));
            var ortsteil = ortsteile.Get(Field(columns.Ortsteil));
            into.Add(east, north,
                streets.Get(Field(columns.Street)),
                houseNumbers.Get(Field(columns.HouseNumber)),
                houseNumbers.Get(Field(columns.HouseNumberSuffix)),
                postcodes.Get(Field(columns.Postcode)),
                ortsteil ?? gemeinde,
                gemeinde);
            accepted++;
        }

        return new HkReadResult(rows, accepted, otherQuality, malformed);
    }

    private static bool IsAllowed(ReadOnlySpan<char> quality, IReadOnlyCollection<string> allowed)
    {
        foreach (var candidate in allowed)
        {
            if (quality.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>The candidate delimiter the header line uses most, outside quotes.</summary>
    public static char DetectDelimiter(string header)
    {
        var counts = new int[CandidateDelimiters.Length];
        var quoted = false;
        foreach (var c in header)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && Array.IndexOf(CandidateDelimiters, c) is var i and >= 0) counts[i]++;
        }
        var best = Array.IndexOf(counts, counts.Max());
        return counts[best] > 0
            ? CandidateDelimiters[best]
            : throw new InspireImportException($"The Hauskoordinaten file's header has no delimiter: \"{header}\".");
    }

    /// <summary>
    /// Splits one line into fields, unquoting as it goes: each field's text is written to
    /// <paramref name="buffer"/> (never longer than the line) and its range there to
    /// <paramref name="fields"/>. Fields beyond <paramref name="fields"/>' length are counted
    /// but not stored. Lenient where a stricter parser would throw: text after a closing quote
    /// is kept, an unterminated quote runs to the end of the line.
    /// </summary>
    /// <returns>The number of fields on the line.</returns>
    internal static int SplitFields(ReadOnlySpan<char> line, char delimiter, Span<char> buffer, Span<Range> fields)
    {
        var count = 0;
        var written = 0;
        var i = 0;
        while (true)
        {
            var start = written;
            if (i < line.Length && line[i] == '"')
            {
                for (i++; i < line.Length; i++)
                {
                    if (line[i] != '"')
                    {
                        buffer[written++] = line[i];
                    }
                    else if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        buffer[written++] = '"';
                        i++;
                    }
                    else
                    {
                        i++;
                        break;
                    }
                }
            }
            while (i < line.Length && line[i] != delimiter)
                buffer[written++] = line[i++];

            if (count < fields.Length) fields[count] = start..written;
            count++;
            if (i >= line.Length) return count;
            i++; // the delimiter
        }
    }

    /// <summary>The indices of the columns the import reads; -1 for an optional one that's missing.</summary>
    private sealed class Columns
    {
        public int Count { get; private init; }
        public int Quality { get; private init; }
        public int Gemeinde { get; private init; }
        public int Ortsteil { get; private init; }
        public int Street { get; private init; }
        public int HouseNumber { get; private init; }
        public int HouseNumberSuffix { get; private init; }
        public int Zone { get; private init; }
        public int East { get; private init; }
        public int North { get; private init; }
        public int Postcode { get; private init; }

        public static Columns FromHeader(string header, char delimiter)
        {
            var buffer = new char[header.Length];
            var ranges = new Range[header.Length + 1];
            var count = SplitFields(header, delimiter, buffer, ranges);
            var names = ranges[..count].Select(r => buffer.AsSpan(r).Trim().ToString().ToLowerInvariant()).ToList();

            int Optional(string name) => names.IndexOf(name);
            int Required(string name) => names.IndexOf(name) is var index and >= 0
                ? index
                : throw new InspireImportException(
                    $"The Hauskoordinaten file has no \"{name}\" column; its header is \"{header}\".");

            return new Columns
            {
                Count = count,
                Quality = Required("qua"),
                Gemeinde = Required("gmd"),
                Street = Required("str"),
                HouseNumber = Required("hnr"),
                East = Required("ostwert"),
                North = Required("nordwert"),
                Ortsteil = Optional("ott"),
                HouseNumberSuffix = Optional("adz"),
                Zone = Optional("zone"),
                Postcode = Optional("postplz"),
            };
        }
    }

    /// <summary>
    /// A district that is only a number: Hessen's file gives 42,654 addresses in Frankfurt and
    /// Darmstadt the Ortsteil "Frankfurt Bezirk 1" … "Frankfurt Bezirk 33" and "Darmstadt Bezirk 1"
    /// … "Darmstadt Bezirk 6" (every one of its Ortsteil names that contains a digit). The real
    /// district names ("Sachsenhausen", "Bessungen") are elsewhere in the same file.
    /// </summary>
    [GeneratedRegex(@"^.+ Bezirk \d+$")]
    private static partial Regex NumberedDistrict();

    /// <summary>
    /// A text value per distinct span, made (and transformed) once on first sight. Blank spans
    /// are null without a lookup.
    /// </summary>
    private sealed class SpanCache(Func<string, string?> create)
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

        public string? Get(ReadOnlySpan<char> span)
        {
            if (span.IsEmpty) return null;
            var lookup = _values.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(span, out var value)) return value;

            var key = span.ToString();
            value = create(key);
            _values[key] = value;
            return value;
        }
    }
}
