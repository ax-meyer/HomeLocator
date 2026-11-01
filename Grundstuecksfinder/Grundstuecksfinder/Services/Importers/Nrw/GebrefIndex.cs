using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Grundstuecksfinder.Services.Importers.Postcodes;
using NetTopologySuite.Geometries;

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
/// strings' few hundred). A key found at two or more different points is the same street name in
/// several Ortsteile of one Gemeinde: ~6,000 addresses, 86 % of them in Duisburg, which has 289
/// street names belonging to several streets. Their points are kept as candidates, and
/// <see cref="ResolvePostcodes"/> lets <see cref="Find"/> pick the one in the row's PLZ.
/// </para>
/// </remarks>
public sealed class GebrefIndex
{
    private const int UtmEpsg = 25832;

    private const int GemeindeField = 10;
    private const int StreetField = 14;
    private const int HouseNumberField = 15;
    private const int SuffixField = 16;
    private const int ZoneField = 17;
    private const int EastField = 18;
    private const int NorthField = 19;
    private const int MinFields = 20;

    // Points closer than this (~1 m) are one point: see SamePoint.
    private const float SamePointDegrees = 1e-5f;

    // "str." ending a word: "Schlaraffiastr.", "Öckendorfer Str.".
    private static readonly Regex StreetAbbreviation = new(@"str\.(?=\s|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<ulong, (float Lat, float Lon)> _points;
    private readonly Dictionary<ulong, Candidate[]> _candidates;

    private GebrefIndex(Dictionary<ulong, (float Lat, float Lon)> points, Dictionary<ulong, Candidate[]> candidates, int skipped, Envelope extent)
    {
        _points = points;
        _candidates = candidates;
        Skipped = skipped;
        Extent = extent;
    }

    /// <summary>Addresses with exactly one point.</summary>
    public int Count => _points.Count;

    /// <summary>Addresses GEBREF lists at two or more different points; only their PLZ tells them apart.</summary>
    public int Ambiguous => _candidates.Count;

    /// <summary>Lines that weren't an address in EPSG 25832 (malformed, or another zone).</summary>
    public int Skipped { get; }

    /// <summary>The addresses' extent in EPSG 25832: where postcode areas are needed.</summary>
    public Envelope Extent { get; }

    /// <summary>Reads GEBREF's text file; fails if fewer than half its lines are usable addresses.</summary>
    public static async Task<GebrefIndex> LoadAsync(Stream gebrefTxt, CancellationToken ct)
    {
        var toLonLat = new UtmToLonLat(25832);
        var points = new Dictionary<ulong, (float Lat, float Lon)>(capacity: 5_000_000);
        var several = new Dictionary<ulong, List<(float Lat, float Lon)>>();
        var extent = new Envelope();
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

            extent.ExpandToInclude(east, north);
            var key = Key(fields[GemeindeField], fields[StreetField], fields[HouseNumberField], fields[SuffixField]);
            var (lon, lat) = toLonLat.Transform(east, north);
            (float Lat, float Lon) point = ((float)lat, (float)lon);
            if (points.TryAdd(key, point)) continue;

            if (several.TryGetValue(key, out var known))
            {
                if (known.TrueForAll(p => !SamePoint(p, point))) known.Add(point);
            }
            else if (!SamePoint(points[key], point))
            {
                several.Add(key, [points[key], point]);
            }
        }

        // A format change would otherwise leave NRW without coordinates, silently.
        if (lines == 0 || skipped > lines / 2)
            throw new InvalidDataException($"GEBREF: only {lines - skipped} of {lines} lines are addresses in EPSG 25832; has the format changed?");

        foreach (var key in several.Keys)
            points.Remove(key);
        points.TrimExcess();
        var candidates = several.ToDictionary(e => e.Key, e => e.Value.Select(p => new Candidate(p.Lat, p.Lon)).ToArray());
        return new GebrefIndex(points, candidates, skipped, extent);
    }

    // Points closer than ~1 m are one point: the same address listed twice for one building.
    private static bool SamePoint((float Lat, float Lon) a, (float Lat, float Lon) b) =>
        Math.Abs(a.Lat - b.Lat) + Math.Abs(a.Lon - b.Lon) <= SamePointDegrees;

    /// <summary>
    /// Gives every candidate point of an <see cref="Ambiguous"/> address the postcode of the area
    /// it lies in; returns how many addresses that makes unique for at least one PLZ.
    /// </summary>
    /// <param name="postcodes">Postcode areas in EPSG 25832.</param>
    public int ResolvePostcodes(IPostcodeLookup postcodes)
    {
        var toUtm = new UtmToLonLat(UtmEpsg);
        var resolvable = 0;
        foreach (var candidates in _candidates.Values)
        {
            foreach (var candidate in candidates)
            {
                var (x, y) = toUtm.ToUtm(candidate.Lon, candidate.Lat);
                candidate.Postcode = postcodes.FindPostcode(new Point(x, y));
            }
            if (candidates.Any(c => c.Postcode is not null && candidates.Count(o => o.Postcode == c.Postcode) == 1))
                resolvable++;
        }
        return resolvable;
    }

    /// <summary>
    /// The address's point, or null where GEBREF has none. Where it has several, the one in
    /// <paramref name="postcode"/>'s area (once <see cref="ResolvePostcodes"/> ran), or null when
    /// none or several lie there.
    /// </summary>
    public GebrefMatch? Find(string? gemeinde, string? street, string? houseNumber, string? suffix, string? postcode = null)
    {
        var key = Key(gemeinde, street, houseNumber, suffix);
        if (_points.TryGetValue(key, out var point))
            return new GebrefMatch(point.Lat, point.Lon, ByPostcode: false);
        if (postcode is null || !_candidates.TryGetValue(key, out var candidates))
            return null;

        Candidate? hit = null;
        foreach (var candidate in candidates)
        {
            if (candidate.Postcode != postcode) continue;
            if (hit is not null) return null;
            hit = candidate;
        }
        return hit is null ? null : new GebrefMatch(hit.Lat, hit.Lon, ByPostcode: true);
    }

    /// <summary>One of an ambiguous address's points; the postcode is filled in by <see cref="ResolvePostcodes"/>.</summary>
    private sealed class Candidate(float lat, float lon)
    {
        public float Lat { get; } = lat;
        public float Lon { get; } = lon;
        public string? Postcode { get; set; }
    }

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

/// <summary>
/// A GEBREF point in WGS84; <paramref name="ByPostcode"/> when the address is listed at several
/// and the row's PLZ picked this one.
/// </summary>
public readonly record struct GebrefMatch(float Lat, float Lon, bool ByPostcode);
