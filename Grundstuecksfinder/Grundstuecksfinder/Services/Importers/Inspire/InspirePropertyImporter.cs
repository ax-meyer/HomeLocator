using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Grundstuecksfinder.Models;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// Imports an INSPIRE-split Bundesland: joins the separate cp:CadastralParcel (area+geometry)
/// and ad:Address (text+geometry) WFS services by point-in-polygon, since neither carries both.
/// One <see cref="InspireSourceOptions"/> instance = one state. Tiling over the state's bounding
/// box happens inside <see cref="FetchAsync"/> only to bound memory — <see cref="DiscoverAsync"/>
/// always returns a single whole-state candidate, because PropertyBulkWriter deletes all of a
/// source's rows on every write, so one candidate per tile would wipe previously-imported tiles.
/// </summary>
public partial class InspirePropertyImporter(
    ILogger<InspirePropertyImporter> logger,
    IHttpClientFactory httpClientFactory,
    InspireSourceOptions options) : IPropertyImporter
{
    public string Source => options.Source;

    public async Task<IReadOnlyList<ImportCandidate>> DiscoverAsync(CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient();

        long parcelHits;
        long addressHits;
        try
        {
            parcelHits = await GetHitsAsync(http, options.ParcelWfsUrl, "cp:CadastralParcel", ct);
            addressHits = await GetHitsAsync(http, options.AddressWfsUrl, "ad:Address", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogHitsCheckFailed(logger, ex, Source);
            return [];
        }

        if (parcelHits == 0 || addressHits == 0)
        {
            LogNoFeatures(logger, Source);
            return [];
        }

        var versionTimestamp = FormattableString.Invariant($"{parcelHits}:{addressHits}");
        return [new ImportCandidate(options.DatasetName, "statewide", versionTimestamp)];
    }

    public async IAsyncEnumerable<Property> FetchAsync(ImportCandidate candidate, [EnumeratorCancellation] CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient();
        var bbox = options.BoundingBox;
        var totalAddresses = 0L;
        var unmatchedAddresses = 0L;

        var rangeX = bbox.MaxX - bbox.MinX;
        var rangeY = bbox.MaxY - bbox.MinY;
        var tilesX = (int)Math.Ceiling(rangeX / options.TileSizeMeters);
        var tilesY = (int)Math.Ceiling(rangeY / options.TileSizeMeters);
        LogFetchingTiles(logger, Source, tilesX, tilesY);
        for (var ix = 0; ix < tilesX; ix++)
        {
            var x = bbox.MinX + ix * options.TileSizeMeters;
            for (var iy = 0; iy < tilesY; iy++)
            {
                LogFetchingTile(logger, ix, iy, tilesX, tilesY);
                var y = bbox.MinY + iy * options.TileSizeMeters;
                ct.ThrowIfCancellationRequested();

                var tileMaxX = Math.Min(x + options.TileSizeMeters, bbox.MaxX);
                var tileMaxY = Math.Min(y + options.TileSizeMeters, bbox.MaxY);
                var tileBbox = FormattableString.Invariant($"{x},{y},{tileMaxX},{tileMaxY}");

                List<(NetTopologySuite.Geometries.Polygon Polygon, double AreaM2)> parcels;
                List<AddressFeature> addresses;
                try
                {
                    parcels = await FetchAllAsync(http, options.ParcelWfsUrl, "cp:CadastralParcel", tileBbox,
                        WfsGmlParser.ParseCadastralParcels, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogParcelFetchFailed(logger, ex, Source, tileBbox);
                    continue;
                }

                if (parcels.Count == 0)
                {
                    LogNoParcelsInTile(logger, tileBbox);
                    continue;
                }

                var index = new ParcelSpatialIndex();
                foreach (var parcel in parcels)
                    index.Add(parcel.Polygon, parcel.AreaM2);

                try
                {
                    addresses = await FetchAllAsync(http, options.AddressWfsUrl, "ad:Address", tileBbox,
                        WfsGmlParser.ParseAddresses, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogAddressFetchFailed(logger, ex, Source, tileBbox);
                    continue;
                }

                foreach (var address in addresses)
                {
                    totalAddresses++;
                    var area = index.FindContainingParcelArea(address.Location);
                    if (area is null)
                    {
                        unmatchedAddresses++;
                        continue;
                    }

                    yield return new Property
                    {
                        Str = address.Str,
                        Hnr = address.Hnr,
                        HnrZus = address.HnrZus,
                        Plz = address.Plz,
                        Ort = address.Ort,
                        Gemeinde = address.Gemeinde,
                        FlaecheAmtl = area.Value,
                    };
                }
            }
        }

        if (totalAddresses > 0)
            LogUnmatchedAddresses(logger, Source, unmatchedAddresses, totalAddresses);
    }

    private static async Task<long> GetHitsAsync(HttpClient http, string baseUrl, string typeName, CancellationToken ct)
    {
        var url = FormattableString.Invariant(
            $"{baseUrl}?service=WFS&version=2.0.0&request=GetFeature&typenames={Uri.EscapeDataString(typeName)}&resultType=hits");
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = XDocument.Load(stream);
        var attr = doc.Root?.Attributes().FirstOrDefault(a => a.Name.LocalName == "numberMatched")?.Value;
        return long.TryParse(attr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private async Task<List<T>> FetchAllAsync<T>(
        HttpClient http, string baseUrl, string typeName, string tileBbox,
        Func<Stream, IEnumerable<T>> parse, CancellationToken ct)
    {
        var results = new List<T>();
        var startIndex = 0;
        byte[]? previousPageBytes = null;
        while (true)
        {
            var resolveSuffix = typeName.StartsWith("ad:", StringComparison.Ordinal) ? "&resolve=local&resolvedepth=2" : "";
            // srsName ensures the response uses the configured CRS even when the server's
            // default differs (e.g. Hessen defaults to EPSG:4258 but supports 25832).
            var url = FormattableString.Invariant(
                $"{baseUrl}?service=WFS&version=2.0.0&request=GetFeature&typenames={Uri.EscapeDataString(typeName)}&bbox={tileBbox},{Uri.EscapeDataString(options.Crs)}&srsName={Uri.EscapeDataString(options.Crs)}&count={options.PageSize}&startIndex={startIndex}{resolveSuffix}");

            using var response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var pageBytes = await response.Content.ReadAsByteArrayAsync(ct);

            // Some WFS servers (e.g. SH's) silently ignore startIndex and always return page 1.
            // Without this check that turns into an infinite loop, re-appending the same page forever.
            if (previousPageBytes is not null && pageBytes.AsSpan().SequenceEqual(previousPageBytes))
            {
                LogStartIndexIgnored(logger, Source, startIndex, typeName, tileBbox);
                break;
            }

            var page = parse(new MemoryStream(pageBytes)).ToList();
            results.AddRange(page);

            if (page.Count < options.PageSize) break;
            previousPageBytes = pageBytes;
            startIndex += options.PageSize;
        }
        return results;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to check feature counts for {Source}")]
    private static partial void LogHitsCheckFailed(ILogger logger, Exception exception, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source} reported zero parcels or addresses, skipping")]
    private static partial void LogNoFeatures(ILogger logger, string source);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: Fetching {TilesX}x{TilesY} tiles")]
    private static partial void LogFetchingTiles(ILogger logger, string source, int tilesX, int tilesY);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fetching tile {Ix},{Iy} of {TilesX}x{TilesY}")]
    private static partial void LogFetchingTile(ILogger logger, int ix, int iy, int tilesX, int tilesY);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: Failed to fetch parcels for tile {TileBbox}, skipping")]
    private static partial void LogParcelFetchFailed(ILogger logger, Exception exception, string source, string tileBbox);

    [LoggerMessage(Level = LogLevel.Information, Message = "No parcels found in tile {TileBbox}")]
    private static partial void LogNoParcelsInTile(ILogger logger, string tileBbox);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: Failed to fetch addresses for tile {TileBbox}, skipping")]
    private static partial void LogAddressFetchFailed(ILogger logger, Exception exception, string source, string tileBbox);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: {Unmatched}/{Total} addresses had no containing parcel")]
    private static partial void LogUnmatchedAddresses(ILogger logger, string source, long unmatched, long total);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: WFS server ignored startIndex={StartIndex} for {TypeName} in tile {TileBbox} (identical page returned) — stopping pagination, results for this tile may be incomplete")]
    private static partial void LogStartIndexIgnored(ILogger logger, string source, int startIndex, string typeName, string tileBbox);
}
