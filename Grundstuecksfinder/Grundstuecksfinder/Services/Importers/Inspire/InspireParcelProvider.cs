namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// The parcel side of every source: parcels (official area + geometry) from the state's parcel
/// WFS, one tile at a time — INSPIRE cp:CadastralParcel, or the feature type the source's
/// <see cref="InspireSourceOptions.ParcelFeatureType"/> names. No startIndex paging: servers
/// ignore it (SH), cap page sizes below what was asked (HE, BW), and report
/// numberReturned/numberMatched unreliably, so a tile is fetched in one request and reported
/// full instead — the caller then splits it.
/// </summary>
public sealed class InspireParcelProvider(WfsFeatureType wfs, ParcelFeatureTypeOptions type)
{
    /// <summary>The feature type, for log messages.</summary>
    public string TypeName => wfs.TypeName;

    /// <summary>The parcel count: approximate, it drifts with every daily cadastral update.</summary>
    public async Task<FingerprintPart> ProbeAsync(CancellationToken ct) =>
        FingerprintPart.FromCount(await wfs.GetHitsAsync(ct), "parcel");

    /// <summary>
    /// The parcel count again, at the start of a fetch that measures its completeness against it:
    /// the probe may have run hours earlier.
    /// </summary>
    public async Task<long> RequireCountAsync(string source, CancellationToken ct) =>
        await wfs.GetHitsAsync(ct)
        ?? throw new InspireImportException($"{source}: the parcel service no longer reports a feature count.");

    /// <summary>Features per request, lowered to the server's advertised CountDefault.</summary>
    public Task<int> GetPageLimitAsync(CancellationToken ct) => wfs.GetPageLimitAsync(ct);

    /// <summary>A tile's parcels, or null when the page came back full and the tile must be split.</summary>
    public async Task<IReadOnlyList<ParcelFeature>?> GetTileAsync(Tile tile, int pageLimit, CancellationToken ct)
    {
        var page = await wfs.GetPageAsync(tile, pageLimit, doc => WfsGmlParser.ParseParcels(doc, type), ct);
        return page.MemberCount >= pageLimit ? null : page.Features;
    }
}
