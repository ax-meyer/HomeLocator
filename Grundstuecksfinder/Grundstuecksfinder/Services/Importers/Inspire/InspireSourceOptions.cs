using System.Globalization;
using System.Text.RegularExpressions;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// Config for one INSPIRE-split Bundesland: a parcel WFS (area+geometry) and an address WFS
/// (text+geometry), joined spatially since neither dataset carries both. Adding a state is
/// adding an entry to "Import:Inspire:Sources" — no new code.
/// </summary>
public partial class InspireSourceOptions
{
    /// <summary>Stable slug for this source, e.g. "sh". Becomes <see cref="InspirePropertyImporter.Source"/>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// False stops importing this source and hides its already-imported rows from the app
    /// (they stay in the database). Keep the entry and flip this rather than deleting it —
    /// a deleted entry's rows would stay visible.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public string DatasetName { get; set; } = string.Empty;

    /// <summary>Base URL of the cp:CadastralParcel WFS (INSPIRE download service).</summary>
    public string ParcelWfsUrl { get; set; } = string.Empty;

    /// <summary>Base URL of the ad:Address WFS (INSPIRE download service).</summary>
    public string AddressWfsUrl { get; set; } = string.Empty;

    /// <summary>
    /// The CRS both services are asked for (srsName) and must answer in, e.g.
    /// "urn:ogc:def:crs:EPSG::25832". Must be an ETRS89/UTM zone (EPSG 25831–25833): tiling
    /// assumes metres with easting first. A response in any other CRS fails the import.
    /// </summary>
    public string Crs { get; set; } = string.Empty;

    public InspireBoundingBox BoundingBox { get; set; } = new();

    /// <summary>Size of the initial fetch tiles, in metres. Tiles that come back full are split.</summary>
    public double TileSizeMeters { get; set; } = 5000;

    /// <summary>
    /// Smallest tile a full one may be split into. A tile still full at this size means more
    /// features than a page can hold in a tiny area; the import fails rather than lose them.
    /// </summary>
    public double MinTileSizeMeters { get; set; } = 50;

    /// <summary>
    /// Max features requested per GetFeature call. The importer never pages via startIndex (some
    /// servers ignore it); a tile returning this many features is split into four instead. Capped
    /// further by the server's advertised CountDefault.
    /// </summary>
    public int PageSize { get; set; } = 5000;

    /// <summary>
    /// For Hamburg/Berlin: the Land itself is the Gemeinde, and the ad:level hierarchy maps
    /// differently (see <see cref="WfsGmlParser"/>).
    /// </summary>
    public bool IsCityState { get; set; }

    /// <summary>
    /// Whether an address's postal town name (PostalDescriptor postName) may serve as its Ort
    /// when no Ortsteil is given. False for sources that carry one arbitrary postName per
    /// postcode (SH: all of Fehmarn is "Petersdorf a. F."); Ort then falls back to the Gemeinde.
    /// </summary>
    public bool UsePostNameAsOrt { get; set; } = true;

    /// <summary>
    /// Limit for one WFS request including reading and parsing the whole response. Covers what
    /// HttpClient.Timeout doesn't when streaming: a server stalling mid-body.
    /// </summary>
    public double RequestTimeoutSeconds { get; set; } = 300;

    /// <summary>Attempts per WFS request (timeouts, 5xx, broken responses) before the import fails.</summary>
    public int MaxAttempts { get; set; } = 6;

    /// <summary>
    /// Wait before the first retry; doubles with every further attempt (with jitter), up to
    /// <see cref="MaxRetryDelaySeconds"/>. The defaults ride out about five minutes of outage.
    /// </summary>
    public double RetryBaseDelaySeconds { get; set; } = 10;

    /// <summary>Upper bound for one retry wait, including a server's Retry-After.</summary>
    public double MaxRetryDelaySeconds { get; set; } = 300;

    /// <summary>
    /// Minimum share of the address service's reported total that must have been fetched, or
    /// the import fails and the previous data stays.
    /// </summary>
    public double MinCompleteness { get; set; } = 0.95;

    /// <summary>
    /// Maximum share of fetched addresses without a containing parcel. Far above the usual few
    /// percent means the join is broken (e.g. wrong CRS), so the import fails.
    /// </summary>
    public double MaxUnmatchedRatio { get; set; } = 0.2;

    /// <summary>The EPSG code of <see cref="Crs"/>, e.g. 25832; null if it names none.</summary>
    public int? CrsEpsgCode => ParseEpsgCode(Crs);

    /// <summary>
    /// Extracts the EPSG code from any of the srsName forms WFS servers use:
    /// "urn:ogc:def:crs:EPSG::25832", "http://www.opengis.net/def/crs/epsg/0/25832", "EPSG:25832",
    /// and the German AdV names "urn:adv:crs:ETRS89_UTM32" (also with a height suffix, like
    /// "ETRS89_UTM32*DE_DHHN2016_NH"), whose horizontal part is EPSG 25831–25833.
    /// </summary>
    public static int? ParseEpsgCode(string? srsName)
    {
        if (string.IsNullOrWhiteSpace(srsName)) return null;
        var match = EpsgCodePattern().Match(srsName.Trim());
        if (match.Success) return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);

        var adv = AdvUtmPattern().Match(srsName);
        return adv.Success ? 25800 + int.Parse(adv.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// Checks a whole "Import:Inspire:Sources" list; returns one message per problem. Run at
    /// startup so a broken config fails the deploy instead of the 03:00 import.
    /// </summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<InspireSourceOptions> sources, IReadOnlyCollection<string> reservedSources)
    {
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in sources)
        {
            var name = string.IsNullOrEmpty(s.Source) ? "(no Source)" : s.Source;
            if (!SourcePattern().IsMatch(s.Source))
                errors.Add($"{name}: Source must be a lowercase slug ([a-z0-9-]).");
            else if (reservedSources.Contains(s.Source) || !seen.Add(s.Source))
                errors.Add($"{name}: Source is used twice; each source's import replaces all rows with that Source.");

            if (string.IsNullOrWhiteSpace(s.DatasetName))
                errors.Add($"{name}: DatasetName is required.");
            if (!IsHttpUrl(s.ParcelWfsUrl))
                errors.Add($"{name}: ParcelWfsUrl must be an absolute http(s) URL.");
            if (!IsHttpUrl(s.AddressWfsUrl))
                errors.Add($"{name}: AddressWfsUrl must be an absolute http(s) URL.");
            if (s.CrsEpsgCode is not (>= 25831 and <= 25833))
                errors.Add($"{name}: Crs must be ETRS89/UTM (EPSG 25831–25833), was \"{s.Crs}\".");

            var b = s.BoundingBox;
            if (!(b.MinX < b.MaxX && b.MinY < b.MaxY))
                errors.Add($"{name}: BoundingBox must have MinX < MaxX and MinY < MaxY.");
            if (!(s.MinTileSizeMeters > 0 && s.TileSizeMeters >= s.MinTileSizeMeters))
                errors.Add($"{name}: need 0 < MinTileSizeMeters <= TileSizeMeters.");
            if (s.PageSize < 1)
                errors.Add($"{name}: PageSize must be positive.");
            if (s.MaxAttempts < 1)
                errors.Add($"{name}: MaxAttempts must be at least 1.");
            if (!(s.RequestTimeoutSeconds > 0))
                errors.Add($"{name}: RequestTimeoutSeconds must be positive.");
            if (s.RetryBaseDelaySeconds < 0 || s.MaxRetryDelaySeconds < s.RetryBaseDelaySeconds)
                errors.Add($"{name}: need 0 <= RetryBaseDelaySeconds <= MaxRetryDelaySeconds.");
            if (s.MinCompleteness is < 0 or > 1)
                errors.Add($"{name}: MinCompleteness must be between 0 and 1.");
            if (s.MaxUnmatchedRatio is < 0 or > 1)
                errors.Add($"{name}: MaxUnmatchedRatio must be between 0 and 1.");
        }
        return errors;
    }

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    [GeneratedRegex(@"epsg(?:::|:|/0/|/)(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex EpsgCodePattern();

    [GeneratedRegex(@"ETRS89_UTM(3[1-3])(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex AdvUtmPattern();

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex SourcePattern();
}

/// <summary>Statewide extent, in the CRS given by <see cref="InspireSourceOptions.Crs"/>, tiled internally for bounded memory use.</summary>
public class InspireBoundingBox
{
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
}
