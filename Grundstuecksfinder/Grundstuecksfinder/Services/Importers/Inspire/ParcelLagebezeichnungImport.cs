using System.Runtime.CompilerServices;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers.Postcodes;
using NetTopologySuite.Geometries;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// Imports a source whose parcels name their own addresses
/// (<see cref="Addresses.AddressSourceType.ParcelLagebezeichnung"/>): one row per address in a
/// parcel's Lagebezeichnung text, with that parcel's official area. Nothing is joined, so an
/// address two parcels both name yields two rows — one per parcel, as the cadastre has it.
/// </summary>
/// <remarks>
/// <para>
/// Walks the state's bounding box tile by tile like <see cref="ParcelAddressJoin"/>, splitting
/// full tiles and skipping ones whose requests keep failing (within
/// <see cref="InspireSourceOptions.MaxFailedTiles"/>). A parcel crossing a tile edge comes back
/// from every tile it touches; only the tile owning its interior point counts it.
/// </para>
/// <para>
/// Completeness is measured in parcels, against the parcel service's own count: most parcels
/// (roads, fields, woods) name no address, so there is no address total to compare with. Where
/// the source publishes no PLZ, a parcel's is the postcode area containing its interior point.
/// </para>
/// <para>
/// Positions: a parcel naming one address pins it at the parcel's interior point. With several,
/// that point would mark none of them, so where the source has buildings
/// (<see cref="InspireSourceOptions.BuildingFeatureType"/>) each address is pinned at the
/// building naming it (see <see cref="BuildingPositions"/>). The buildings are fetched only for
/// tiles that have such parcels, around those parcels. If their requests keep failing, the
/// tile's rows are still imported, those addresses without a position.
/// </para>
/// </remarks>
public sealed partial class ParcelLagebezeichnungImport(
    InspireSourceOptions options,
    ILogger logger,
    InspireParcelProvider parcels,
    IPostcodeAreaProvider? postcodeAreas,
    InspireBuildingProvider? buildings = null)
{
    private const int ProgressLogInterval = 250;

    private string Source => options.Source;

    /// <summary>Streams the rows; throws once at the end if a check fails.</summary>
    public async IAsyncEnumerable<Property> RunAsync(ImportRunContext run, [EnumeratorCancellation] CancellationToken ct)
    {
        var parcelLimit = await parcels.GetPageLimitAsync(ct);
        var postcodes = await LoadPostcodeAreasAsync(ct);
        var expected = await parcels.RequireCountAsync(Source, ct);
        var toLonLat = new UtmToLonLat(options.CrsEpsgCode!.Value);

        var pending = new Stack<Tile>(Tile.Grid(options.BoundingBox, options.TileSizeMeters).Reverse());
        LogFetchingTiles(logger, Source, pending.Count, parcelLimit, parcels.TypeName);

        long parcelCount = 0;
        long addressedParcels = 0;
        long rowCount = 0;
        long unreadParts = 0;
        long plzFilled = 0;
        var pinned = new long[3];
        var buildingAreasFailed = 0;
        var processedTiles = 0;
        var failedTiles = new FailedTileBudget(logger, Source, options.MaxFailedTiles);
        while (pending.TryPop(out var tile))
        {
            ct.ThrowIfCancellationRequested();
            if (++processedTiles % ProgressLogInterval == 0)
                LogProgress(logger, Source, processedTiles, pending.Count, parcelCount, rowCount);

            IReadOnlyList<ParcelFeature>? tileParcels;
            // try/catch around the await only: an iterator may not yield inside one.
            try
            {
                tileParcels = await parcels.GetTileAsync(tile, parcelLimit, ct);
            }
            catch (Exception ex) when (TileWalk.IsSkippableFailure(ex, ct))
            {
                failedTiles.Record(tile, parcels.TypeName, ex);
                continue;
            }
            if (tileParcels is null)
            {
                foreach (var child in TileWalk.Split(tile, options, logger))
                    pending.Push(child);
                continue;
            }

            var owned = new List<(ParcelFeature Parcel, Point Anchor, LagebezeichnungText Text)>();
            foreach (var parcel in tileParcels)
            {
                var anchor = GeometryAnchor.Of(parcel.Geometry);
                // The bbox filter returns every parcel touching the tile; the one owning the
                // parcel's anchor counts it, so a parcel over an edge is imported once.
                if (anchor is null || !tile.Owns(anchor)) continue;

                parcelCount++;
                var text = LagebezeichnungParser.Parse(parcel.Lagebezeichnung);
                unreadParts += text.UnreadParts;
                if (text.Addresses.Count > 0) owned.Add((parcel, anchor, text));
            }

            BuildingPositions? positions = null;
            if (buildings is not null && Around(owned.Where(p => p.Text.Addresses.Count > 1)) is { } area)
            {
                positions = await LoadBuildingsAsync(buildings, area, parcelLimit, ct);
                if (positions is null) buildingAreasFailed++;
            }

            foreach (var (parcel, anchor, text) in owned)
            {
                addressedParcels++;
                var plz = postcodes?.FindPostcode(anchor);
                if (plz is not null) plzFilled += text.Addresses.Count;
                var gemeinde = PlaceNameNormalizer.NormalizePlace(parcel.Gemeinde);
                // No address points here, only the parcel. Its anchor is a fair pin for the one
                // house on it; with several it would mark none of them, so each gets its building.
                var single = text.Addresses.Count == 1;
                foreach (var address in text.Addresses)
                {
                    rowCount++;
                    var point = single ? anchor : positions?.Find(address, parcel.Geometry);
                    var pin = point is null ? ((double Lon, double Lat)?)null : toLonLat.Transform(point.X, point.Y);
                    pinned[(int)(single ? PinnedAt.Parcel : point is null ? PinnedAt.None : PinnedAt.Building)]++;
                    yield return new Property
                    {
                        Str = PlaceNameNormalizer.NormalizeStreet(address.Str),
                        Hnr = address.Hnr,
                        HnrZus = address.HnrZus,
                        Plz = plz,
                        // The ALKIS-vereinfacht parcel knows no Ortsteil, only its Gemeinde.
                        Ort = gemeinde,
                        Gemeinde = gemeinde,
                        FlaecheAmtl = parcel.AreaM2,
                        Lat = (float?)pin?.Lat,
                        Lon = (float?)pin?.Lon,
                    };
                }
            }
        }

        run.AddSkippedParts(failedTiles.Count);
        var completeness = expected == 0 ? 1 : (double)parcelCount / expected;
        LogFetchSummary(logger, Source, parcelCount, expected, completeness, addressedParcels, rowCount, unreadParts, failedTiles.Count);
        if (postcodes is not null)
            LogPlzSummary(logger, Source, plzFilled, rowCount);
        var withoutShare = rowCount == 0 ? 0 : (double)pinned[(int)PinnedAt.None] / rowCount;
        LogPositions(logger, Source, pinned[(int)PinnedAt.Parcel], pinned[(int)PinnedAt.Building], pinned[(int)PinnedAt.None],
            withoutShare, buildingAreasFailed);

        if (completeness < options.MinCompleteness)
            throw new InspireImportException(FormattableString.Invariant(
                $"{Source}: fetched only {parcelCount} of {expected} parcels ({completeness:P1}, minimum {options.MinCompleteness:P0}); keeping the previous data."));

        if (rowCount == 0)
            throw new InspireImportException(
                $"{Source}: no parcel named an address; is ParcelFeatureType.LagebezeichnungField right?");

        if (postcodes is not null && (double)plzFilled / rowCount < options.MinPostcodeFillRatio)
            throw new InspireImportException(FormattableString.Invariant(
                $"{Source}: only {plzFilled} of {rowCount} addresses lie in a postcode area (minimum {options.MinPostcodeFillRatio:P0}); is the area file right?"));
    }

    /// <summary>Where a row's position came from.</summary>
    private enum PinnedAt { Parcel, Building, None }

    /// <summary>
    /// The area to fetch buildings for: around every given parcel, as far as
    /// <see cref="BuildingPositions.MaxDistanceMeters"/> reaches; null without parcels.
    /// </summary>
    private static Tile? Around(IEnumerable<(ParcelFeature Parcel, Point Anchor, LagebezeichnungText Text)> parcels)
    {
        var area = new Envelope();
        foreach (var p in parcels)
            area.ExpandToInclude(p.Parcel.Geometry.EnvelopeInternal);
        if (area.IsNull) return null;

        area.ExpandBy(BuildingPositions.MaxDistanceMeters);
        return new Tile(area.MinX, area.MinY, area.MaxX, area.MaxY);
    }

    /// <summary>
    /// The buildings in <paramref name="area"/>, splitting it where a page comes back full; null
    /// if a request keeps failing. The parcels' page limit applies: it's the same server.
    /// </summary>
    private async Task<BuildingPositions?> LoadBuildingsAsync(InspireBuildingProvider buildings, Tile area, int pageLimit, CancellationToken ct)
    {
        var pending = new Stack<Tile>([area]);
        var found = new List<BuildingFeature>();
        while (pending.TryPop(out var tile))
        {
            IReadOnlyList<BuildingFeature>? page;
            try
            {
                page = await buildings.GetTileAsync(tile, pageLimit, ct);
            }
            catch (Exception ex) when (TileWalk.IsSkippableFailure(ex, ct))
            {
                LogBuildingsFailed(logger, ex, Source, buildings.TypeName, tile.Bbox);
                return null;
            }
            if (page is null)
            {
                foreach (var child in TileWalk.Split(tile, options, logger))
                    pending.Push(child);
                continue;
            }
            found.AddRange(page);
        }
        return new BuildingPositions(found);
    }

    private async Task<IPostcodeLookup?> LoadPostcodeAreasAsync(CancellationToken ct)
    {
        if (!options.FillMissingPlzFromPostcodeAreas) return null;
        if (postcodeAreas is null)
            throw new InvalidOperationException($"{Source}: FillMissingPlzFromPostcodeAreas is set, but no postcode area provider was given.");

        var b = options.BoundingBox;
        return await postcodeAreas.LoadAsync(options.CrsEpsgCode!.Value, new Envelope(b.MinX, b.MaxX, b.MinY, b.MaxY), ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: Fetching {Tiles} tiles, up to {ParcelLimit} {TypeName} per request; addresses from their Lagebezeichnung")]
    private static partial void LogFetchingTiles(ILogger logger, string source, int tiles, int parcelLimit, string typeName);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: {Processed} tiles done, {Pending} pending, {Parcels} parcels and {Rows} addresses so far")]
    private static partial void LogProgress(ILogger logger, string source, int processed, int pending, long parcels, long rows);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: fetched {Fetched} of {Expected} parcels ({Completeness:P1}); {Addressed} named {Rows} addresses, {Unread} Lagebezeichnung parts couldn't be read, {FailedTiles} tiles were skipped after repeated failures")]
    private static partial void LogFetchSummary(ILogger logger, string source, long fetched, long expected, double completeness, long addressed, long rows, long unread, int failedTiles);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: filled the PLZ of {Filled} of {Rows} addresses from postcode areas")]
    private static partial void LogPlzSummary(ILogger logger, string source, long filled, long rows);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: positions: {AtParcel} at their parcel, {AtBuilding} at their building, {Without} without ({WithoutShare:P2}); {FailedAreas} tiles' buildings couldn't be fetched")]
    private static partial void LogPositions(ILogger logger, string source, long atParcel, long atBuilding, long without, double withoutShare, int failedAreas);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: giving up on {TypeName} for {TileBbox}; its parcels' addresses stay without a building position")]
    private static partial void LogBuildingsFailed(ILogger logger, Exception exception, string source, string typeName, string tileBbox);
}
