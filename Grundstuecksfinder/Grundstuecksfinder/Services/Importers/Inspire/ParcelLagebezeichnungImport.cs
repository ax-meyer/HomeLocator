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
/// </remarks>
public sealed partial class ParcelLagebezeichnungImport(
    InspireSourceOptions options,
    ILogger logger,
    InspireParcelProvider parcels,
    IPostcodeAreaProvider? postcodeAreas)
{
    private const int ProgressLogInterval = 250;

    private string Source => options.Source;

    /// <summary>Streams the rows; throws once at the end if a check fails.</summary>
    public async IAsyncEnumerable<Property> RunAsync(ImportRunContext run, [EnumeratorCancellation] CancellationToken ct)
    {
        var parcelLimit = await parcels.GetPageLimitAsync(ct);
        var postcodes = await LoadPostcodeAreasAsync(ct);
        var expected = await parcels.RequireCountAsync(Source, ct);

        var pending = new Stack<Tile>(Tile.Grid(options.BoundingBox, options.TileSizeMeters).Reverse());
        LogFetchingTiles(logger, Source, pending.Count, parcelLimit, parcels.TypeName);

        long parcelCount = 0;
        long addressedParcels = 0;
        long rowCount = 0;
        long unreadParts = 0;
        long plzFilled = 0;
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

            foreach (var parcel in tileParcels)
            {
                var anchor = Anchor(parcel.Geometry);
                // The bbox filter returns every parcel touching the tile; the one owning the
                // parcel's anchor counts it, so a parcel over an edge is imported once.
                if (anchor is null || !tile.Owns(anchor)) continue;

                parcelCount++;
                var text = LagebezeichnungParser.Parse(parcel.Lagebezeichnung);
                unreadParts += text.UnreadParts;
                if (text.Addresses.Count == 0) continue;

                addressedParcels++;
                var plz = postcodes?.FindPostcode(anchor);
                if (plz is not null) plzFilled += text.Addresses.Count;
                var gemeinde = PlaceNameNormalizer.NormalizePlace(parcel.Gemeinde);
                foreach (var address in text.Addresses)
                {
                    rowCount++;
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
                    };
                }
            }
        }

        run.AddSkippedParts(failedTiles.Count);
        var completeness = expected == 0 ? 1 : (double)parcelCount / expected;
        LogFetchSummary(logger, Source, parcelCount, expected, completeness, addressedParcels, rowCount, unreadParts, failedTiles.Count);
        if (postcodes is not null)
            LogPlzSummary(logger, Source, plzFilled, rowCount);

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

    /// <summary>
    /// The point that stands for a parcel: inside it (so the tile owning the point is one the
    /// parcel touches, and its postcode area is the parcel's own), and the same in every tile's
    /// response. A geometry too broken for that falls back to its first vertex; none is null.
    /// </summary>
    private static Point? Anchor(Geometry geometry)
    {
        if (geometry.IsEmpty) return null;
        try
        {
            var point = geometry.InteriorPoint;
            if (point is { IsEmpty: false }) return point;
        }
        catch (TopologyException)
        {
        }
        return geometry.Factory.CreatePoint(geometry.Coordinates[0]);
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
}
