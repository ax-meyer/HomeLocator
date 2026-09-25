namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// The rules every tile loop over a state's bounding box shares (<see cref="ParcelAddressJoin"/>,
/// <see cref="ParcelLagebezeichnungImport"/>): when a full tile is split, and which failures a
/// tile may be skipped for.
/// </summary>
internal static partial class TileWalk
{
    /// <summary>
    /// Whether a tile's failure may be skipped rather than fail the import: the transient errors
    /// whose retries are by now spent. A rejection by the circuit breaker is deliberately not one
    /// — it means the service as a whole is failing, which must stop the import at once instead
    /// of burning through the tile budget. Neither is a cancelled import, nor a check on the data
    /// itself (wrong CRS, a tile still full at the minimum size), which no retry would fix.
    /// </summary>
    public static bool IsSkippableFailure(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && InspireServiceClient.IsTransient(ex);

    /// <summary>The four quarters of a full tile, in the order they should be pushed.</summary>
    public static Tile[] Split(Tile tile, InspireSourceOptions options, ILogger logger)
    {
        if (tile.Size / 2 < options.MinTileSizeMeters)
            throw new InspireImportException(
                $"{options.Source}: tile {tile.Bbox} is still full at the minimum tile size; features would be lost.");

        LogSplitTile(logger, options.Source, tile.Bbox);
        return tile.Quarters();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Source}: tile {TileBbox} is full, splitting it into four")]
    private static partial void LogSplitTile(ILogger logger, string source, string tileBbox);
}

/// <summary>
/// Counts the tiles given up on and enforces <see cref="InspireSourceOptions.MaxFailedTiles"/>:
/// one permanently broken tile must not throw away a run's hours of fetched data, while a
/// service failing all over must not pass as an import.
/// </summary>
internal sealed partial class FailedTileBudget(ILogger logger, string source, int max)
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: giving up on {What} for tile {TileBbox} and skipping it ({Failed} of at most {MaxFailed} tiles skipped)")]
    private static partial void LogTileSkipped(ILogger logger, Exception exception, string source, string what, string tileBbox, int failed, int maxFailed);
}
