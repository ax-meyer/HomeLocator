using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Grundstuecksfinder.Services.Importers.Nrw;

/// <summary>
/// NRW's Gebäudereferenzen (GEBREF): the official building coordinate of every address, looked
/// up by address because the Grundsteuer CSV has none. GEBREF has no PLZ, so the key is
/// Gemeinde, street, house number and suffix.
/// </summary>
/// <remarks>
/// <para>
/// The file is ~4.5 M ';'-separated lines without a header, one per address, e.g.
/// <c>N;DENW01HK10000a4O;B;05;Nordrhein-Westfalen;9;Arnsberg;11;Bochum;000;Bochum;0000;;04176;Wilhelm-Leithe-Weg;76;a;32;370473.340;5703695.059;2026-07-01</c>:
/// field 10 is the Gemeinde, 14 the street, 15/16 number and suffix, 17 the UTM zone, 18/19
/// east and north in EPSG 25832.
/// </para>
/// <para>
/// Only a 64-bit hash of each key is kept with its point (~30 B per address instead of the
/// strings' few hundred). A key found at two different points (the same street name in two
/// Ortsteile of one Gemeinde) has no single answer and is dropped.
/// </para>
/// </remarks>
public sealed class GebrefIndex
{
    private const int GemeindeField = 10;
    private const int StreetField = 14;
    private const int HouseNumberField = 15;
    private const int SuffixField = 16;
    private const int ZoneField = 17;
    private const int EastField = 18;
    private const int NorthField = 19;
    private const int MinFields = 20;

    // Points closer than this (~1 m) are one point: the same address listed twice for one building.
    private const float SamePointDegrees = 1e-5f;

    // "str." ending a word: "Schlaraffiastr.", "Öckendorfer Str.".
    private static readonly Regex StreetAbbreviation = new(@"str\.(?=\s|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<ulong, (float Lat, float Lon)> _points;

    private GebrefIndex(Dictionary<ulong, (float Lat, float Lon)> points, int ambiguous, int skipped)
    {
        _points = points;
        Ambiguous = ambiguous;
        Skipped = skipped;
    }

    /// <summary>Addresses with exactly one point.</summary>
    public int Count => _points.Count;

    /// <summary>Addresses dropped because GEBREF lists them at two or more different points.</summary>
    public int Ambiguous { get; }

    /// <summary>Lines that weren't an address in EPSG 25832 (malformed, or another zone).</summary>
    public int Skipped { get; }

    /// <summary>Reads GEBREF's text file; fails if fewer than half its lines are usable addresses.</summary>
    public static async Task<GebrefIndex> LoadAsync(Stream gebrefTxt, CancellationToken ct)
    {
        var toLonLat = new UtmToLonLat(25832);
        var points = new Dictionary<ulong, (float Lat, float Lon)>(capacity: 5_000_000);
        var ambiguous = new HashSet<ulong>();
        var lines = 0;
        var skipped = 0;

        using var reader = new StreamReader(gebrefTxt, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            lines++;
            var fields = line.Split(';');
            if (fields.Length < MinFields || fields[ZoneField] != "32"
                || !double.TryParse(fields[EastField], NumberStyles.Float, CultureInfo.InvariantCulture, out var east)
                || !double.TryParse(fields[NorthField], NumberStyles.Float, CultureInfo.InvariantCulture, out var north))
            {
                skipped++;
                continue;
            }

            var key = Key(fields[GemeindeField], fields[StreetField], fields[HouseNumberField], fields[SuffixField]);
            var (lon, lat) = toLonLat.Transform(east, north);
            (float Lat, float Lon) point = ((float)lat, (float)lon);
            if (!points.TryAdd(key, point))
            {
                var first = points[key];
                if (Math.Abs(first.Lat - point.Lat) + Math.Abs(first.Lon - point.Lon) > SamePointDegrees)
                    ambiguous.Add(key);
            }
        }

        // A format change would otherwise leave NRW without coordinates, silently.
        if (lines == 0 || skipped > lines / 2)
            throw new InvalidDataException($"GEBREF: only {lines - skipped} of {lines} lines are addresses in EPSG 25832; has the format changed?");

        foreach (var key in ambiguous)
            points.Remove(key);
        points.TrimExcess();
        return new GebrefIndex(points, ambiguous.Count, skipped);
    }

    /// <summary>The address's point, or null where GEBREF has none or several.</summary>
    public (float Lat, float Lon)? Find(string? gemeinde, string? street, string? houseNumber, string? suffix) =>
        _points.TryGetValue(Key(gemeinde, street, houseNumber, suffix), out var point) ? point : null;

    /// <summary>
    /// The hash both sides are compared by. GEBREF abbreviates a tenth of its street names
    /// ("Schlaraffiastr.", "Öckendorfer Str.") where the Grundsteuer CSV writes "-straße"; case,
    /// ß/ss, hyphens and spacing differ too ("2 A" vs "2a"), so all of that is normalized away.
    /// </summary>
    internal static ulong Key(string? gemeinde, string? street, string? houseNumber, string? suffix)
    {
        var hash = Fnv.OffsetBasis;
        hash = Fnv.Add(hash, Normalize(gemeinde, keepSpaces: true));
        hash = Fnv.Add(hash, NormalizeStreet(street));
        hash = Fnv.Add(hash, Normalize(houseNumber, keepSpaces: false));
        return Fnv.Add(hash, Normalize(suffix, keepSpaces: false));
    }

    internal static string NormalizeStreet(string? street) =>
        StreetAbbreviation.Replace(Normalize(street, keepSpaces: true), "strasse");

    /// <summary>Lower case, ß as ss, hyphens as spaces, runs of spaces as one (or none).</summary>
    private static string Normalize(string? value, bool keepSpaces)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var sb = new StringBuilder(value.Length + 4);
        var pendingSpace = false;
        foreach (var c in value.Trim())
        {
            if (char.IsWhiteSpace(c) || c == '-')
            {
                pendingSpace = keepSpaces;
                continue;
            }
            if (pendingSpace && sb.Length > 0) sb.Append(' ');
            pendingSpace = false;
            if (c == 'ß') sb.Append("ss");
            else sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>64-bit FNV-1a: enough bits that 4.5 M keys practically never collide (~1e-6).</summary>
    private static class Fnv
    {
        public const ulong OffsetBasis = 14695981039346656037;
        private const ulong Prime = 1099511628211;

        public static ulong Add(ulong hash, string part)
        {
            foreach (var c in part)
            {
                hash = (hash ^ (byte)c) * Prime;
                hash = (hash ^ (byte)(c >> 8)) * Prime;
            }
            // A separator, so ("ab", "c") and ("a", "bc") differ.
            return (hash ^ 0x1F) * Prime;
        }
    }
}
