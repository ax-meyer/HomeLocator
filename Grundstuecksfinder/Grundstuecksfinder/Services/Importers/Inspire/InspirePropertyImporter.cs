using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers.Postcodes;
using NetTopologySuite.Geometries;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// Imports an INSPIRE-split Bundesland: joins the separate cp:CadastralParcel (area+geometry)
/// and ad:Address (text+geometry) WFS services by point-in-polygon, since neither carries both.
/// One <see cref="InspireSourceOptions"/> instance = one state. The address side can instead come
/// from an OGC API Features "items" endpoint carrying an ALKIS-native schema (<see
/// cref="InspireSourceOptions.UseOgcApiAddresses"/>), for a state whose INSPIRE address WFS is
/// too slow to page through — the parcel side always stays on the INSPIRE WFS.
/// </summary>
/// <remarks>
/// <para>
/// Tiling over the state's bounding box happens inside <see cref="FetchAsync"/> only to bound
/// memory — <see cref="DiscoverAsync"/> always returns a single whole-state candidate, because
/// <see cref="PropertyBulkWriter"/> replaces all of a source's rows per write.
/// </para>
/// <para>
/// No startIndex paging: servers ignore it (SH), cap page sizes below what was asked (HE, BW),
/// and report numberReturned/numberMatched unreliably (HE and BW stream "0"/"unknown"), so pages
/// can't be trusted to be complete or consistent. Instead each tile is fetched in one request
/// and, if that request comes back full, split into four smaller tiles.
/// </para>
/// <para>
/// A tile whose requests still fail after every retry is logged and skipped rather than losing
/// the whole (hours-long) run — the completeness check below is the safety net, and one tile of
/// thousands is far inside it. Past <see cref="InspireSourceOptions.MaxFailedTiles"/> skipped
/// tiles the import fails after all, so a server that is broken everywhere can't quietly import
/// half a state.
/// </para>
/// <para>
/// Every other failure — an error document instead of features, a response in the wrong CRS, a
/// tile still full at the minimum size, too few addresses overall, too many without a parcel —
/// throws, so the writer never swaps in partial data and the source's previous rows stay.
/// </para>
/// </remarks>
public partial class InspirePropertyImporter(
    ILogger<InspirePropertyImporter> logger,
    IHttpClientFactory httpClientFactory,
    InspireSourceOptions options,
    TimeProvider? timeProvider = null,
    IPostcodeAreaProvider? postcodeAreas = null,
    NameCatalogLoader? nameCatalogLoader = null,
    ILoggerFactory? loggerFactory = null) : IPropertyImporter
{
    /// <summary>Named HttpClient for the WFS requests; per-request limits come from the options.</summary>
    public const string HttpClientName = "Inspire";

    private const string ParcelType = "cp:CadastralParcel";
    private const string AddressType = "ad:Address";
    private const int ProgressLogInterval = 250;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Retry → circuit breaker → per-attempt timeout, one pipeline per source so a server that is
    /// down only trips its own state. Built lazily because it needs <see cref="_time"/>.
    /// </summary>
    private ResiliencePipeline? _pipeline;

    public string Source => options.Source;

    /// <summary>
    /// Tiles the last <see cref="FetchAsync"/> gave up on. Written once its enumeration has run
    /// to the end, so the orchestrator can record a tolerated hole on the ImportRun.
    /// </summary>
    public int SkippedTiles { get; private set; }

    public async Task<IReadOnlyList<ImportCandidate>> DiscoverAsync(CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);

        long? parcelHits;
        long? addressHits;
        try
        {
            parcelHits = await GetHitsAsync(http, options.ParcelWfsUrl, ParcelType, ct);
            addressHits = options.UseOgcApiAddresses
                ? await GetOgcApiHitsAsync(http, options.AddressWfsUrl, ct)
                : await GetHitsAsync(http, options.AddressWfsUrl, AddressType, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogHitsCheckFailed(logger, ex, Source);
            return [];
        }

        // Without a total there's nothing to check the import's completeness against.
        if (parcelHits is null || addressHits is null)
        {
            LogHitsUnknown(logger, Source);
            return [];
        }

        if (parcelHits == 0 || addressHits == 0)
        {
            LogNoFeatures(logger, Source);
            return [];
        }

        // The counts catch most upstream changes; the month forces a re-import at least monthly
        // for the ones they don't (renamings, corrected areas, equal adds and removes).
        var month = _time.GetUtcNow().ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var versionTimestamp = FormattableString.Invariant($"{parcelHits}:{addressHits}:{month}");
        return [new ImportCandidate(options.DatasetName, "statewide", versionTimestamp)];
    }

    public async IAsyncEnumerable<Property> FetchAsync(ImportCandidate candidate, [EnumeratorCancellation] CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);

        var expectedAddresses = (options.UseOgcApiAddresses
                ? await GetOgcApiHitsAsync(http, options.AddressWfsUrl, ct)
                : await GetHitsAsync(http, options.AddressWfsUrl, AddressType, ct))
            ?? throw new InspireImportException($"{Source}: the address service no longer reports a feature count.");

        var parcelLimit = await GetPageLimitAsync(http, options.ParcelWfsUrl, ct);
        var addressLimit = options.UseOgcApiAddresses
            ? options.OgcApiAddressPageSize
            : await GetPageLimitAsync(http, options.AddressWfsUrl, ct);
        // Loaded before the (long) fetch, so a missing area file fails the import right away.
        var postcodes = await LoadPostcodeAreasAsync(ct);
        var nameCatalog = options.NameCatalog.IsConfigured && nameCatalogLoader is not null
            ? await nameCatalogLoader.LoadAsync(options.NameCatalog, ct)
            : null;
        var pagedAddresses = options.PageAddressesWithStartIndex
            ? await FetchAllAddressesAsync(http, addressLimit, expectedAddresses, nameCatalog, ct)
            : options.UseOgcApiAddresses
                ? await FetchAllAddressesFromOgcApiAsync(http, expectedAddresses, ct)
                : null;
        var plzStats = new PlzFillStats();

        // A tile may carry its parcels along: when only the address page of a tile was full, its
        // (complete) parcel page is filtered down to the child tiles instead of fetched again.
        var pending = new Stack<(Tile Tile, IReadOnlyList<ParcelFeature>? Parcels)>(
            InitialTiles().Reverse().Select(t => (t, (IReadOnlyList<ParcelFeature>?)null)));
        LogFetchingTiles(logger, Source, pending.Count, parcelLimit, addressLimit);

        long addressCount = 0;
        long unmatchedCount = 0;
        var processedTiles = 0;
        var failedTiles = new FailedTileBudget(logger, Source, options.MaxFailedTiles);
        SkippedTiles = 0;
        while (pending.TryPop(out var item))
        {
            ct.ThrowIfCancellationRequested();
            var tile = item.Tile;
            if (++processedTiles % ProgressLogInterval == 0)
                LogProgress(logger, Source, processedTiles, pending.Count, addressCount);

            var parcels = item.Parcels;
            if (parcels is null)
            {
                WfsPage<ParcelFeature> parcelPage;
                // try/catch around the await only: an iterator may not yield inside one.
                try
                {
                    parcelPage = await FetchPageAsync(http, options.ParcelWfsUrl, ParcelType, tile, parcelLimit,
                        WfsGmlParser.ParseCadastralParcels, ct);
                }
                catch (Exception ex) when (IsSkippableTileFailure(ex, ct))
                {
                    failedTiles.Record(tile, ParcelType, ex);
                    continue;
                }
                if (parcelPage.MemberCount >= parcelLimit)
                {
                    foreach (var child in Split(tile))
                        pending.Push((child, null));
                    continue;
                }
                parcels = parcelPage.Features;
            }
            if (parcels.Count == 0) continue;

            IReadOnlyList<AddressFeature> tileAddresses;
            if (pagedAddresses is not null)
            {
                // Already in memory, so a tile is never too full for its addresses.
                tileAddresses = pagedAddresses.Query(new Envelope(tile.MinX, tile.MaxX, tile.MinY, tile.MaxY));
            }
            else
            {
                WfsPage<AddressFeature> addresses;
                try
                {
                    addresses = await FetchPageAsync(http, options.AddressWfsUrl, AddressType, tile, addressLimit,
                        doc => WfsGmlParser.ParseAddresses(doc, options.IsCityState, options.UsePostNameAsOrt, nameCatalog), ct);
                }
                catch (Exception ex) when (IsSkippableTileFailure(ex, ct))
                {
                    failedTiles.Record(tile, AddressType, ex);
                    continue;
                }
                if (addresses.MemberCount >= addressLimit)
                {
                    foreach (var child in Split(tile))
                        pending.Push((child, parcels.Where(p => child.Intersects(p.Geometry.EnvelopeInternal)).ToList()));
                    continue;
                }
                tileAddresses = addresses.Features;
            }

            var index = new ParcelSpatialIndex();
            foreach (var parcel in parcels)
                index.Add(parcel.Geometry, parcel.AreaM2);

            foreach (var address in tileAddresses)
            {
                // The bbox filter includes a tile's edges, so an address exactly on a border
                // comes back from both neighbours; only the tile owning it (half-open) counts it.
                if (!tile.Owns(address.Location)) continue;

                addressCount++;
                var area = index.FindContainingParcelArea(address.Location);
                if (area is null)
                {
                    unmatchedCount++;
                    continue;
                }

                yield return new Property
                {
                    Str = address.Str,
                    Hnr = address.Hnr,
                    HnrZus = address.HnrZus,
                    Plz = postcodes is null ? address.Plz : plzStats.Resolve(address, postcodes),
                    Ort = address.Ort,
                    Gemeinde = address.Gemeinde,
                    FlaecheAmtl = area.Value,
                };
            }
        }

        SkippedTiles = failedTiles.Count;
        var completeness = expectedAddresses == 0 ? 1 : (double)addressCount / expectedAddresses;
        LogFetchSummary(logger, Source, addressCount, expectedAddresses, completeness, unmatchedCount, failedTiles.Count);
        if (postcodes is not null)
            LogPlzSummary(logger, Source, plzStats.Filled, plzStats.Missing, plzStats.Agreeing, plzStats.Official);
        if (nameCatalog is not null)
            LogNameRepairs(logger, Source, nameCatalog.Repaired, nameCatalog.Unrepairable);

        if (completeness < options.MinCompleteness)
            throw new InspireImportException(FormattableString.Invariant(
                $"{Source}: fetched only {addressCount} of {expectedAddresses} addresses ({completeness:P1}, minimum {options.MinCompleteness:P0}); keeping the previous data."));

        if (addressCount > 0 && (double)unmatchedCount / addressCount > options.MaxUnmatchedRatio)
            throw new InspireImportException(FormattableString.Invariant(
                $"{Source}: {unmatchedCount} of {addressCount} addresses have no containing parcel (maximum {options.MaxUnmatchedRatio:P0}) — is the Crs right?"));

        if (postcodes is not null && plzStats.Missing > 0 && (double)plzStats.Filled / plzStats.Missing < options.MinPostcodeFillRatio)
            throw new InspireImportException(FormattableString.Invariant(
                $"{Source}: only {plzStats.Filled} of {plzStats.Missing} addresses without a PLZ lie in a postcode area (minimum {options.MinPostcodeFillRatio:P0}); is the area file right?"));
    }

    private async Task<IPostcodeLookup?> LoadPostcodeAreasAsync(CancellationToken ct)
    {
        if (!options.FillMissingPlzFromPostcodeAreas) return null;
        if (postcodeAreas is null)
            throw new InvalidOperationException($"{Source}: FillMissingPlzFromPostcodeAreas is set, but no postcode area provider was given.");

        var b = options.BoundingBox;
        return await postcodeAreas.LoadAsync(options.CrsEpsgCode!.Value, new Envelope(b.MinX, b.MaxX, b.MinY, b.MaxY), ct);
    }

    /// <summary>
    /// Whether a tile's failure may be skipped rather than fail the import: the transient errors
    /// whose retries are by now spent. A rejection by the circuit breaker is deliberately not one
    /// — it means the service as a whole is failing, which must stop the import at once instead
    /// of burning through the tile budget. Neither is a cancelled import, nor a check on the data
    /// itself (wrong CRS, a tile still full at the minimum size), which no retry would fix.
    /// </summary>
    private static bool IsSkippableTileFailure(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && IsTransient(ex);

    /// <summary>
    /// Counts the tiles given up on and enforces <see cref="InspireSourceOptions.MaxFailedTiles"/>:
    /// one permanently broken tile must not throw away a run's hours of fetched data, while a
    /// service failing all over must not pass as an import.
    /// </summary>
    private sealed class FailedTileBudget(ILogger logger, string source, int max)
    {
        public int Count { get; private set; }

        public void Record(Tile tile, string typeName, Exception failure)
        {
            Count++;
            LogTileSkipped(logger, failure, source, typeName, tile.Bbox, Count, max);
            if (Count > max)
                throw new InspireImportException(FormattableString.Invariant(
                    $"{source}: gave up on {Count} tiles (maximum {max}); keeping the previous data."), failure);
        }
    }

    /// <summary>Fills missing PLZ from the postcode areas and counts how that went.</summary>
    private sealed class PlzFillStats
    {
        public long Missing { get; private set; }
        public long Filled { get; private set; }
        public long Official { get; private set; }
        public long Agreeing { get; private set; }

        public string? Resolve(AddressFeature address, IPostcodeLookup postcodes)
        {
            var fromArea = postcodes.FindPostcode(address.Location);
            if (string.IsNullOrEmpty(address.Plz))
            {
                Missing++;
                if (fromArea is not null) Filled++;
                return fromArea;
            }

            // The source's own PLZ wins; comparing shows how far the areas can be trusted.
            Official++;
            if (fromArea == address.Plz) Agreeing++;
            return address.Plz;
        }
    }

    private IEnumerable<Tile> InitialTiles()
    {
        var bbox = options.BoundingBox;
        for (var x = bbox.MinX; x < bbox.MaxX; x += options.TileSizeMeters)
        {
            for (var y = bbox.MinY; y < bbox.MaxY; y += options.TileSizeMeters)
            {
                yield return new Tile(x, y,
                    Math.Min(x + options.TileSizeMeters, bbox.MaxX),
                    Math.Min(y + options.TileSizeMeters, bbox.MaxY));
            }
        }
    }

    /// <summary>The four quarters of a full tile, in the order they should be pushed.</summary>
    private Tile[] Split(Tile tile)
    {
        if (Math.Max(tile.MaxX - tile.MinX, tile.MaxY - tile.MinY) / 2 < options.MinTileSizeMeters)
            throw new InspireImportException(
                $"{Source}: tile {tile.Bbox} is still full at the minimum tile size; features would be lost.");

        LogSplitTile(logger, Source, tile.Bbox);
        var midX = (tile.MinX + tile.MaxX) / 2;
        var midY = (tile.MinY + tile.MaxY) / 2;
        // Pushed onto a stack, so the reverse of the order they'll be processed in.
        return
        [
            new Tile(midX, midY, tile.MaxX, tile.MaxY),
            new Tile(tile.MinX, midY, midX, tile.MaxY),
            new Tile(midX, tile.MinY, tile.MaxX, midY),
            new Tile(tile.MinX, tile.MinY, midX, midY),
        ];
    }

    /// <summary>
    /// Fetches every address in one pass, paging with startIndex, for a service whose bbox filter
    /// is unusable (Hamburg: its address geometries carry SRID 0, so the server rejects every
    /// bbox as "mixed SRID geometries"). Paging is only safe where a page is the same across
    /// requests and adjacent pages don't overlap — verified for Hamburg before enabling it there.
    /// </summary>
    private async Task<AddressSpatialIndex> FetchAllAddressesAsync(
        HttpClient http, int pageSize, long expected, NameCatalog? nameCatalog, CancellationToken ct)
    {
        var all = new List<AddressFeature>();
        // A server that silently ignores startIndex would hand out its first page forever.
        var limit = expected + pageSize;
        for (var startIndex = 0L; ; startIndex += pageSize)
        {
            var filter = FormattableString.Invariant($"&startIndex={startIndex}");
            var what = FormattableString.Invariant($"startIndex={startIndex}");
            var page = await FetchPageAsync(http, options.AddressWfsUrl, AddressType, filter, what, pageSize,
                doc => WfsGmlParser.ParseAddresses(doc, options.IsCityState, options.UsePostNameAsOrt, nameCatalog), ct);
            all.AddRange(page.Features);

            if (all.Count > limit)
                throw new InspireImportException(FormattableString.Invariant(
                    $"{Source}: paging the address service passed {all.Count} addresses although it reports {expected}; is startIndex ignored?"));
            if (page.MemberCount < pageSize) break;
        }

        LogPagedAddresses(logger, Source, all.Count, expected);
        return new AddressSpatialIndex(all);
    }

    /// <summary>
    /// Fetches every address in one pass from an OGC API Features "items" endpoint, paging with
    /// limit/offset, for a source whose INSPIRE address WFS is usable but far too slow (Saarland:
    /// ~5 addr/s at any tile size vs ~800 addr/s here). See <see cref="OgcApiAddressParser"/>.
    /// </summary>
    private async Task<AddressSpatialIndex> FetchAllAddressesFromOgcApiAsync(HttpClient http, long expected, CancellationToken ct)
    {
        var pageSize = options.OgcApiAddressPageSize;
        var all = new List<AddressFeature>();
        // A server that silently ignores offset would hand out its first page forever.
        var limit = expected + pageSize;
        for (var offset = 0L; ; offset += pageSize)
        {
            var url = FormattableString.Invariant($"{options.AddressWfsUrl}?limit={pageSize}&offset={offset}");
            var what = FormattableString.Invariant($"offset={offset}");
            var page = await WithRetryAsync(async token =>
            {
                using var doc = await GetJsonAsync(http, url, token);
                return OgcApiAddressParser.ParseAddresses(doc);
            }, AddressType, what, ct);
            all.AddRange(page.Features);

            if (all.Count > limit)
                throw new InspireImportException(FormattableString.Invariant(
                    $"{Source}: paging the address service passed {all.Count} addresses although it reports {expected}; is offset ignored?"));
            if (page.MemberCount < pageSize) break;
        }

        LogPagedAddresses(logger, Source, all.Count, expected);
        return new AddressSpatialIndex(all);
    }

    /// <summary>numberMatched from an OGC API Features "items" endpoint, via the smallest allowed page.</summary>
    private async Task<long?> GetOgcApiHitsAsync(HttpClient http, string baseUrl, CancellationToken ct)
    {
        var url = FormattableString.Invariant($"{baseUrl}?limit=1");
        return await WithRetryAsync(async token =>
        {
            using var doc = await GetJsonAsync(http, url, token);
            return doc.RootElement.TryGetProperty("numberMatched", out var value) && value.TryGetInt64(out var matched)
                ? matched : (long?)null;
        }, AddressType, "hits", ct);
    }

    private Task<WfsPage<T>> FetchPageAsync<T>(
        HttpClient http, string baseUrl, string typeName, Tile tile, int count,
        Func<XDocument, WfsPage<T>> parse, CancellationToken ct)
    {
        var crs = Uri.EscapeDataString(options.Crs);
        var filter = FormattableString.Invariant($"&bbox={tile.Bbox},{crs}");
        return FetchPageAsync(http, baseUrl, typeName, filter, tile.Bbox, count, parse, ct);
    }

    private async Task<WfsPage<T>> FetchPageAsync<T>(
        HttpClient http, string baseUrl, string typeName, string filter, string what, int count,
        Func<XDocument, WfsPage<T>> parse, CancellationToken ct)
    {
        var resolveSuffix = typeName == AddressType ? "&resolve=local&resolvedepth=2" : "";
        // srsName ensures the response uses the configured CRS even when the server's
        // default differs (e.g. Hessen defaults to EPSG:4258 but supports 25832).
        var crs = Uri.EscapeDataString(options.Crs);
        var url = FormattableString.Invariant(
            $"{baseUrl}?service=WFS&version=2.0.0&request=GetFeature&typenames={Uri.EscapeDataString(typeName)}{filter}&srsName={crs}&count={count}{resolveSuffix}");

        var page = await WithRetryAsync(async token =>
        {
            var doc = await GetXmlAsync(http, url, token);
            EnsureCompleteFeatureCollection(doc, url);
            return parse(doc);
        }, typeName, what, ct);

        // Unrecognised srsNames count as foreign too: an unchecked CRS could silently misjoin.
        var expectedEpsg = options.CrsEpsgCode;
        var foreign = page.SrsNames.Where(name => InspireSourceOptions.ParseEpsgCode(name) != expectedEpsg).ToList();
        if (foreign.Count > 0)
            throw new InspireImportException(
                $"{Source}: {typeName} answered in {string.Join(", ", foreign)} instead of EPSG:{expectedEpsg}.");

        return page;
    }

    /// <summary>
    /// Features per request: <see cref="InspireSourceOptions.PageSize"/>, lowered to the server's
    /// advertised CountDefault — a server silently capping below what was asked would otherwise
    /// make full tiles look complete.
    /// </summary>
    private async Task<int> GetPageLimitAsync(HttpClient http, string baseUrl, CancellationToken ct)
    {
        var url = $"{baseUrl}?service=WFS&version=2.0.0&request=GetCapabilities";
        try
        {
            var serverLimit = await WithRetryAsync(
                async token => ParseCountDefault(await GetXmlAsync(http, url, token)), "GetCapabilities", baseUrl, ct);

            return serverLimit is > 0 && serverLimit < options.PageSize ? (int)serverLimit : options.PageSize;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The completeness check still catches a server capping pages below PageSize.
            LogCapabilitiesFailed(logger, ex, Source, baseUrl);
            return options.PageSize;
        }
    }

    public static long? ParseCountDefault(XDocument capabilities)
    {
        var constraint = capabilities.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Constraint" &&
                                 (string?)e.Attribute("name") == "CountDefault");
        var value = constraint?.Descendants()
            .FirstOrDefault(e => e.Name.LocalName is "DefaultValue" or "Value")?.Value;
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) ? limit : null;
    }

    private async Task<long?> GetHitsAsync(HttpClient http, string baseUrl, string typeName, CancellationToken ct)
    {
        var url = FormattableString.Invariant(
            $"{baseUrl}?service=WFS&version=2.0.0&request=GetFeature&typenames={Uri.EscapeDataString(typeName)}&resultType=hits");
        return await WithRetryAsync(async token =>
        {
            var doc = await GetXmlAsync(http, url, token);
            EnsureCompleteFeatureCollection(doc, url);
            var attr = doc.Root!.Attributes().FirstOrDefault(a => a.Name.LocalName == "numberMatched")?.Value;
            return long.TryParse(attr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : (long?)null;
        }, typeName, "hits", ct);
    }

    private async Task<XDocument> GetXmlAsync(HttpClient http, string url, CancellationToken ct)
    {
        await using var body = await FetchBodyAsync(http, url, ct);
        return await XDocument.LoadAsync(body, LoadOptions.None, ct);
    }

    private async Task<JsonDocument> GetJsonAsync(HttpClient http, string url, CancellationToken ct)
    {
        // Without an explicit Accept, Saarland's OGC API Features server answers its HTML
        // viewer instead of GeoJSON.
        await using var body = await FetchBodyAsync(http, url, ct, "application/geo+json");
        return await JsonDocument.ParseAsync(body, cancellationToken: ct);
    }

    private async Task<MemoryStream> FetchBodyAsync(HttpClient http, string url, CancellationToken ct, string? accept = null)
    {
        await ThrottleAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (accept is not null) request.Headers.Accept.ParseAdd(accept);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var retryAfter = response.Headers.RetryAfter is { } header
                ? header.Delta ?? (header.Date - DateTimeOffset.UtcNow)
                : null;
            throw new WfsHttpException(response.StatusCode, retryAfter, url);
        }
        // Buffer first: CopyToAsync passes the token to every read, so the per-attempt timeout
        // can abort a body that stalls. XDocument.LoadAsync/JsonDocument.ParseAsync don't hand
        // their token to the stream, so parsing straight from the network could hang forever.
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var body = new MemoryStream();
        await stream.CopyToAsync(body, ct);
        body.Position = 0;
        return body;
    }

    /// <summary>
    /// Servers answer errors (overload, internal exceptions) with an ows:ExceptionReport or a
    /// truncated collection, often with HTTP 200. Parsed as-is that would be an empty tile.
    /// </summary>
    private static void EnsureCompleteFeatureCollection(XDocument doc, string url)
    {
        var root = doc.Root!;
        if (root.Name.LocalName != "FeatureCollection")
            throw new WfsResponseException($"Expected a FeatureCollection but got {root.Name.LocalName} from {url}: {Truncate(root.Value)}");
        if (root.Elements().Any(e => e.Name.LocalName == "truncatedResponse"))
            throw new WfsResponseException($"The server truncated its response to {url}.");
    }

    private static string Truncate(string text) => text.Length <= 300 ? text.Trim() : text[..300].Trim() + "…";

    private async Task<T> WithRetryAsync<T>(
        Func<CancellationToken, Task<T>> action, string what, string where, CancellationToken ct)
    {
        var context = ResilienceContextPool.Shared.Get(ct);
        context.Properties.Set(WhatKey, what);
        context.Properties.Set(WhereKey, where);
        try
        {
            return await (_pipeline ??= BuildPipeline()).ExecuteAsync(
                static async (ctx, state) => await state(ctx.CancellationToken), context, action);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    /// <summary>
    /// Earliest instant the next request may go out. One source issues its requests strictly one
    /// at a time (the tile loop awaits each), so a single instant is enough and needs no lock.
    /// </summary>
    private DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Holds the source to at most one request per
    /// <see cref="InspireSourceOptions.MinRequestIntervalSeconds"/>. A whole state is tens of
    /// thousands of requests against one public download service, and nothing else in the fetch
    /// paces them: without this the loop runs as fast as the server answers. Retries are paced
    /// too, since every attempt goes through here.
    /// </summary>
    private async Task ThrottleAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(options.MinRequestIntervalSeconds);
        if (interval <= TimeSpan.Zero) return;

        var wait = _nextRequestAt - _time.GetUtcNow();
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, _time, ct);
        _nextRequestAt = _time.GetUtcNow() + interval;
    }

    private static readonly ResiliencePropertyKey<string> WhatKey = new("what");
    private static readonly ResiliencePropertyKey<string> WhereKey = new("where");

    /// <summary>
    /// Exponential backoff with jitter, never below a server's Retry-After and never above
    /// <see cref="InspireSourceOptions.MaxRetryDelaySeconds"/>; then a circuit breaker so a server
    /// that is down fails the import in seconds instead of retrying every one of thousands of
    /// tiles; innermost a per-attempt timeout that also bounds reading and parsing the body,
    /// which HttpClient.Timeout stops covering once the headers have arrived.
    /// </summary>
    private ResiliencePipeline BuildPipeline()
    {
        var builder = new ResiliencePipelineBuilder { TimeProvider = _time, Name = $"inspire:{Source}" };
        // Polly's own logs and metrics (meter "Polly", tagged with the pipeline name), next to
        // the app's meter in AppMetrics. Left off when no factory is available, e.g. in tests.
        if (loggerFactory is not null)
            builder.ConfigureTelemetry(loggerFactory);
        return builder
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome.Exception)),
                MaxRetryAttempts = options.MaxAttempts - 1,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(options.RetryBaseDelaySeconds),
                MaxDelay = TimeSpan.FromSeconds(options.MaxRetryDelaySeconds),
                DelayGenerator = args =>
                {
                    // Polly's own delay is in args.Context; a server's Retry-After wins when longer.
                    var retryAfter = (args.Outcome.Exception as WfsHttpException)?.RetryAfter;
                    return ValueTask.FromResult(retryAfter is { } wait
                        ? TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds, options.MaxRetryDelaySeconds))
                        : (TimeSpan?)null);
                },
                OnRetry = args =>
                {
                    LogRetrying(logger, args.Outcome.Exception!, Source,
                        args.Context.Properties.GetValue(WhatKey, "request"),
                        args.Context.Properties.GetValue(WhereKey, ""),
                        args.AttemptNumber + 1, options.MaxAttempts, args.RetryDelay);
                    return default;
                },
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome.Exception)),
                FailureRatio = options.CircuitFailureRatio,
                MinimumThroughput = options.CircuitMinimumThroughput,
                SamplingDuration = TimeSpan.FromSeconds(options.CircuitSamplingSeconds),
                BreakDuration = TimeSpan.FromSeconds(options.CircuitBreakSeconds),
                OnOpened = args =>
                {
                    LogCircuitOpened(logger, args.Outcome.Exception!, Source, args.BreakDuration);
                    return default;
                },
                OnClosed = args =>
                {
                    LogCircuitClosed(logger, Source);
                    return default;
                },
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
            })
            .Build();
    }

    /// <summary>
    /// <see cref="TransientErrors"/> plus what only a WFS/OGC API response can go wrong with: a
    /// body that is truncated, garbled or an error document instead of a feature collection.
    /// </summary>
    private static bool IsTransient(Exception? ex) => ex switch
    {
        WfsHttpException { StatusCode: { } status } => TransientErrors.IsTransientStatus(status),
        XmlException or JsonException or WfsResponseException => true,
        _ => TransientErrors.IsTransient(ex),
    };

    /// <summary>A fetch tile; bounds in the source's CRS.</summary>
    private readonly record struct Tile(double MinX, double MinY, double MaxX, double MaxY)
    {
        public string Bbox => FormattableString.Invariant($"{MinX},{MinY},{MaxX},{MaxY}");

        /// <summary>Half-open, so each point belongs to exactly one of two adjacent tiles.</summary>
        public bool Owns(Point p) => p.X >= MinX && p.X < MaxX && p.Y >= MinY && p.Y < MaxY;

        /// <summary>Closed, like the WFS bbox filter: what a request for this tile would return.</summary>
        public bool Intersects(Envelope e) => e.MinX <= MaxX && e.MaxX >= MinX && e.MinY <= MaxY && e.MaxY >= MinY;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to check feature counts for {Source}")]
    private static partial void LogHitsCheckFailed(ILogger logger, Exception exception, string source);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Source} doesn't report feature counts (numberMatched), so an import's completeness can't be checked; skipping")]
    private static partial void LogHitsUnknown(ILogger logger, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source} reported zero parcels or addresses, skipping")]
    private static partial void LogNoFeatures(ILogger logger, string source);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: paged {Fetched} of {Expected} addresses in one pass, before joining them to parcels")]
    private static partial void LogPagedAddresses(ILogger logger, string source, int fetched, long expected);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: repaired {Repaired} damaged names from the catalogue; {Unrepairable} kept as published")]
    private static partial void LogNameRepairs(ILogger logger, string source, int repaired, int unrepairable);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: Fetching {Tiles} tiles, up to {ParcelLimit} parcels / {AddressLimit} addresses per request")]
    private static partial void LogFetchingTiles(ILogger logger, string source, int tiles, int parcelLimit, int addressLimit);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: {Processed} tiles done, {Pending} pending, {Addresses} addresses so far")]
    private static partial void LogProgress(ILogger logger, string source, int processed, int pending, long addresses);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Source}: tile {TileBbox} is full, splitting it into four")]
    private static partial void LogSplitTile(ILogger logger, string source, string tileBbox);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: giving up on {What} for tile {TileBbox} and skipping it ({Failed} of at most {MaxFailed} tiles skipped)")]
    private static partial void LogTileSkipped(ILogger logger, Exception exception, string source, string what, string tileBbox, int failed, int maxFailed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: {What} for {Where} failed (attempt {Attempt} of {MaxAttempts}), retrying in {Delay}")]
    private static partial void LogRetrying(ILogger logger, Exception exception, string source, string what, string where, int attempt, int maxAttempts, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Source}: too many requests failed; pausing all requests for {BreakDuration}")]
    private static partial void LogCircuitOpened(ILogger logger, Exception exception, string source, TimeSpan breakDuration);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: requests are getting through again")]
    private static partial void LogCircuitClosed(ILogger logger, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: couldn't read the page-size limit from {Url}'s capabilities; using the configured PageSize")]
    private static partial void LogCapabilitiesFailed(ILogger logger, Exception exception, string source, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: fetched {Fetched} of {Expected} addresses ({Completeness:P1}); {Unmatched} had no containing parcel, {FailedTiles} tiles were skipped after repeated failures")]
    private static partial void LogFetchSummary(ILogger logger, string source, long fetched, long expected, double completeness, long unmatched, int failedTiles);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: filled the PLZ of {Filled} of {Missing} addresses without one from postcode areas; the areas agree with {Agreeing} of {Official} PLZ the source publishes")]
    private static partial void LogPlzSummary(ILogger logger, string source, long filled, long missing, long agreeing, long official);
}

/// <summary>
/// The fetched data is incomplete or inconsistent (wrong CRS, too few addresses, a tile that
/// can't be fetched completely). Not retried; the import fails and the previous data stays.
/// </summary>
public sealed class InspireImportException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>A WFS request answered with a non-success status, and the server's Retry-After if any.</summary>
public sealed class WfsHttpException(HttpStatusCode statusCode, TimeSpan? retryAfter, string url)
    : HttpRequestException($"{(int)statusCode} {statusCode} from {url}", null, statusCode)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>A WFS answered with something other than a complete FeatureCollection. Retried.</summary>
public sealed class WfsResponseException(string message) : Exception(message);
