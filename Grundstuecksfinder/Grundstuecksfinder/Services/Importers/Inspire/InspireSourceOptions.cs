using System.Globalization;
using System.Text.RegularExpressions;
using Grundstuecksfinder.Services.Importers.Inspire.Addresses;
using Grundstuecksfinder.Services.Importers.Scheduling;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// Config for one INSPIRE-split Bundesland: a parcel WFS (area+geometry) and an address source
/// (text+geometry), joined spatially since neither dataset carries both. Adding a state is
/// adding an entry to "Import:Inspire:Sources" — no new code.
/// </summary>
public partial class InspireSourceOptions
{
    /// <summary>Stable slug for this source, e.g. "sh". Becomes <see cref="InspirePropertyImporter.Id"/>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// False stops importing this source and hides its already-imported rows from the app
    /// (they stay in the database). Keep the entry and flip this rather than deleting it —
    /// a deleted entry's rows would stay visible.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// This state's own refresh ages; unset values fall back to "Import:Refresh". Its hit-count
    /// fingerprint is Approximate, so both ages apply (see <see cref="RefreshPolicy"/>).
    /// </summary>
    public RefreshOverride? Refresh { get; set; }

    /// <summary>Base URL of the cp:CadastralParcel WFS (INSPIRE download service).</summary>
    public string ParcelWfsUrl { get; set; } = string.Empty;

    /// <summary>
    /// Where the addresses (text + point) come from. The parcel side is always the INSPIRE
    /// WFS above; the address side is whichever of the state's datasets is usable and fastest.
    /// </summary>
    public AddressSourceOptions AddressSource { get; set; } = new();

    /// <summary>
    /// The CRS the WFS services are asked for (srsName) and must answer in, and that the
    /// addresses from any other source must be in, e.g. "urn:ogc:def:crs:EPSG::25832". Must be
    /// an ETRS89/UTM zone (EPSG 25831–25833): tiling assumes metres with easting first. Data in
    /// any other CRS fails the import.
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
    /// Max features requested per WFS GetFeature call. Parcels are never paged via startIndex
    /// (some servers ignore it); a tile returning this many features is split into four instead.
    /// Capped further by the server's advertised CountDefault.
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
    /// For sources that publish (almost) no PLZ (BB, NI, BW, HE): an address without one gets the PLZ of
    /// the postcode area containing it (see "Import:PostcodeAreas"). Addresses that do carry a
    /// PLZ keep it; they're only compared with the areas, as a logged plausibility check.
    /// </summary>
    public bool FillMissingPlzFromPostcodeAreas { get; set; }

    /// <summary>
    /// With <see cref="FillMissingPlzFromPostcodeAreas"/>: minimum share of PLZ-less addresses
    /// that must lie in some postcode area, or the import fails (broken or foreign area file).
    /// </summary>
    public double MinPostcodeFillRatio { get; set; } = 0.95;

    /// <summary>
    /// For Hessen, whose address export replaced every non-ASCII character with U+FFFD: where to
    /// look up the intact spellings (see <see cref="NameCatalogLoader"/>). Leave unset elsewhere.
    /// </summary>
    public NameCatalogOptions NameCatalog { get; set; } = new();

    /// <summary>
    /// Limit for one WFS request including reading and parsing the whole response. Covers what
    /// HttpClient.Timeout doesn't when streaming: a server stalling mid-body.
    /// </summary>
    public double RequestTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Shortest gap between two requests to this source, so a full state — thousands of tiles,
    /// two requests each — stays at a rate a public download service can be expected to absorb.
    /// Requests are issued one at a time per source anyway, so this is a ceiling on the rate,
    /// not a queue. 0 disables it. The wait counts towards <see cref="RequestTimeoutSeconds"/>,
    /// which matters only if the two are set anywhere near each other.
    /// </summary>
    public double MinRequestIntervalSeconds { get; set; } = 0.5;

    /// <summary>
    /// Attempts per WFS request (timeouts, 5xx, broken responses) before the request is given up
    /// on. A tile whose requests are given up on is skipped, up to <see cref="MaxFailedTiles"/>.
    /// </summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>
    /// Wait before the first retry; doubles with every further attempt (with jitter), up to
    /// <see cref="MaxRetryDelaySeconds"/>. The defaults ride out about an hour of outage —
    /// long enough for a service's nightly maintenance window, and the longest one request may
    /// hold up the import before its tile is skipped.
    /// </summary>
    public double RetryBaseDelaySeconds { get; set; } = 10;

    /// <summary>Upper bound for one retry wait, including a server's Retry-After.</summary>
    public double MaxRetryDelaySeconds { get; set; } = 900;

    /// <summary>
    /// How many tiles may be skipped after their requests kept failing before the import fails
    /// anyway. One broken tile out of a state's thousands is far inside
    /// <see cref="MinCompleteness"/>, so it must not throw away hours of fetched data; a server
    /// failing everywhere must still fail the import instead of silently importing half a state.
    /// Every skipped tile costs up to a full retry budget, so keep this small.
    /// </summary>
    public int MaxFailedTiles { get; set; } = 10;

    /// <summary>
    /// Share of failing requests within <see cref="CircuitSamplingSeconds"/> that opens the
    /// circuit for this source, so a server that is down fails the import in seconds instead of
    /// retrying every one of thousands of tiles.
    /// </summary>
    public double CircuitFailureRatio { get; set; } = 0.9;

    /// <summary>Requests needed in the sampling window before the ratio is considered at all.</summary>
    public int CircuitMinimumThroughput { get; set; } = 10;

    /// <summary>Window the failure ratio is measured over.</summary>
    public double CircuitSamplingSeconds { get; set; } = 60;

    /// <summary>How long the circuit stays open before a single trial request is let through.</summary>
    public double CircuitBreakSeconds { get; set; } = 30;

    /// <summary>
    /// Minimum share of the address source's own total (see
    /// <see cref="Addresses.ITileAddresses.ExpectedCount"/>) that must have been joined, or the
    /// import fails and the previous data stays.
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

            if (!IsHttpUrl(s.ParcelWfsUrl))
                errors.Add($"{name}: ParcelWfsUrl must be an absolute http(s) URL.");
            errors.AddRange(s.AddressSource.Validate().Select(e => $"{name}: AddressSource.{e}"));
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
            if (s.MaxFailedTiles < 0)
                errors.Add($"{name}: MaxFailedTiles must not be negative.");
            if (!(s.RequestTimeoutSeconds > 0))
                errors.Add($"{name}: RequestTimeoutSeconds must be positive.");
            if (s.MinRequestIntervalSeconds < 0)
                errors.Add($"{name}: MinRequestIntervalSeconds must not be negative.");
            if (s.RetryBaseDelaySeconds < 0 || s.MaxRetryDelaySeconds < s.RetryBaseDelaySeconds)
                errors.Add($"{name}: need 0 <= RetryBaseDelaySeconds <= MaxRetryDelaySeconds.");
            if (s.CircuitFailureRatio is <= 0 or > 1)
                errors.Add($"{name}: CircuitFailureRatio must be greater than 0 and at most 1.");
            if (s.CircuitMinimumThroughput < 2)
                errors.Add($"{name}: CircuitMinimumThroughput must be at least 2.");
            if (s.CircuitSamplingSeconds < 0.5)
                errors.Add($"{name}: CircuitSamplingSeconds must be at least 0.5.");
            if (!(s.CircuitBreakSeconds > 0))
                errors.Add($"{name}: CircuitBreakSeconds must be positive.");
            if (s.MinCompleteness is < 0 or > 1)
                errors.Add($"{name}: MinCompleteness must be between 0 and 1.");
            if (s.MaxUnmatchedRatio is < 0 or > 1)
                errors.Add($"{name}: MaxUnmatchedRatio must be between 0 and 1.");
            if (s.MinPostcodeFillRatio is < 0 or > 1)
                errors.Add($"{name}: MinPostcodeFillRatio must be between 0 and 1.");
        }
        return errors;
    }

    internal static bool IsHttpUrl(string url) =>
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
