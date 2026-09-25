using System.Runtime.CompilerServices;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers.Inspire.Addresses;
using Grundstuecksfinder.Services.Importers.Postcodes;
using NetTopologySuite.Geometries;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// Joins a source's addresses to the parcel containing each (point-in-polygon), since neither
/// dataset carries both an address and an official area. Walks the state's bounding box tile by
/// tile: parcels are only ever held for one tile, addresses either per tile too or preloaded,
/// depending on the <see cref="IAddressProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// A tile whose parcel page — or, for a per-tile address source, whose address page — comes back
/// full is split into four and its quarters fetched instead. When only the address page was
/// full, the tile's (complete) parcels are handed down to the quarters instead of fetched again.
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
public sealed partial class ParcelAddressJoin(
    InspireSourceOptions options,
    ILogger logger,
    InspireParcelProvider parcels,
    IAddressProvider addresses,
    IPostcodeAreaProvider? postcodeAreas)
{
    private const int ProgressLogInterval = 250;

    private string Source => options.Source;

    /// <summary>Streams the joined rows; throws once at the end if a check fails.</summary>
    /// <param name="probe">This source's probe from the run being imported.</param>
    /// <param name="run">
    /// Receives the tiles given up on, and — when the address side loaded another version than
    /// the probe saw — the fingerprint of what is really imported, both before the stream ends.
    /// </param>
    /// <param name="ct">Cancels the whole run.</param>
    public async IAsyncEnumerable<Property> RunAsync(
        InspireProbe probe, ImportRunContext run, [EnumeratorCancellation] CancellationToken ct)
    {
        var parcelLimit = await parcels.GetPageLimitAsync(ct);
        // Loaded before the addresses and the (long) tile loop, so a missing area file fails
        // the import right away.
        var postcodes = await LoadPostcodeAreasAsync(ct);
        var probedAddresses = probe.Addresses
            ?? throw new ArgumentException($"{Source}: the probe has no address part to join.", nameof(probe));
        var tileAddresses = await addresses.LoadAsync(probedAddresses, ct);
        // Recorded on the run instead of the probe's fingerprint, so the next run compares with
        // the edition actually served rather than importing it a second time.
        if (tileAddresses.Loaded is { } loaded && loaded != probedAddresses)
            run.ReportFingerprint(InspireProbe.Combine(probe.Parcels, loaded).Fingerprint);
        var plzStats = new PlzFillStats();

        // A tile may carry its parcels along: when only the address page of a tile was full, its
        // (complete) parcel page is filtered down to the child tiles instead of fetched again.
        var pending = new Stack<(Tile Tile, IReadOnlyList<ParcelFeature>? Parcels)>(
            Tile.Grid(options.BoundingBox, options.TileSizeMeters).Reverse().Select(t => (t, (IReadOnlyList<ParcelFeature>?)null)));
        LogFetchingTiles(logger, Source, pending.Count, parcelLimit, tileAddresses.Description);

        long addressCount = 0;
        long unmatchedCount = 0;
        var processedTiles = 0;
        var failedTiles = new FailedTileBudget(logger, Source, options.MaxFailedTiles);
        while (pending.TryPop(out var item))
        {
            ct.ThrowIfCancellationRequested();
            var tile = item.Tile;
            if (++processedTiles % ProgressLogInterval == 0)
                LogProgress(logger, Source, processedTiles, pending.Count, addressCount);

            var tileParcels = item.Parcels;
            if (tileParcels is null)
            {
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
                        pending.Push((child, null));
                    continue;
                }
            }
            if (tileParcels.Count == 0) continue;

            AddressTile found;
            try
            {
                found = await tileAddresses.GetAsync(tile, ct);
            }
            catch (Exception ex) when (TileWalk.IsSkippableFailure(ex, ct))
            {
                failedTiles.Record(tile, WfsFeatureType.AddressType, ex);
                continue;
            }
            if (found.IsFull)
            {
                foreach (var child in TileWalk.Split(tile, options, logger))
                    pending.Push((child, tileParcels.Where(p => child.Intersects(p.Geometry.EnvelopeInternal)).ToList()));
                continue;
            }

            var index = new ParcelSpatialIndex();
            foreach (var parcel in tileParcels)
                index.Add(parcel.Geometry, parcel.AreaM2);

            foreach (var address in found.Features)
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

        run.AddSkippedParts(failedTiles.Count);
        var expected = tileAddresses.ExpectedCount;
        var completeness = expected == 0 ? 1 : (double)addressCount / expected;
        LogFetchSummary(logger, Source, addressCount, expected, completeness, unmatchedCount, failedTiles.Count);
        if (postcodes is not null)
            LogPlzSummary(logger, Source, plzStats.Filled, plzStats.Missing, plzStats.Agreeing, plzStats.Official);

        if (completeness < options.MinCompleteness)
            throw new InspireImportException(FormattableString.Invariant(
                $"{Source}: fetched only {addressCount} of {expected} addresses ({completeness:P1}, minimum {options.MinCompleteness:P0}); keeping the previous data."));

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

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: Fetching {Tiles} tiles, up to {ParcelLimit} parcels per request; addresses {Addresses}")]
    private static partial void LogFetchingTiles(ILogger logger, string source, int tiles, int parcelLimit, string addresses);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: {Processed} tiles done, {Pending} pending, {Addresses} addresses so far")]
    private static partial void LogProgress(ILogger logger, string source, int processed, int pending, long addresses);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: fetched {Fetched} of {Expected} addresses ({Completeness:P1}); {Unmatched} had no containing parcel, {FailedTiles} tiles were skipped after repeated failures")]
    private static partial void LogFetchSummary(ILogger logger, string source, long fetched, long expected, double completeness, long unmatched, int failedTiles);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: filled the PLZ of {Filled} of {Missing} addresses without one from postcode areas; the areas agree with {Agreeing} of {Official} PLZ the source publishes")]
    private static partial void LogPlzSummary(ILogger logger, string source, long filled, long missing, long agreeing, long official);
}
