namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// The parcel side of every source: cp:CadastralParcel (official area + geometry) from the
/// state's INSPIRE WFS, one tile at a time. No startIndex paging: servers ignore it (SH), cap
/// page sizes below what was asked (HE, BW), and report numberReturned/numberMatched
/// unreliably, so a tile is fetched in one request and reported full instead — the join then
/// splits it (see <see cref="ParcelAddressJoin"/>).
/// </summary>
public sealed class InspireParcelProvider(WfsFeatureType wfs)
{
    /// <summary>The parcel count: approximate, it drifts with every daily cadastral update.</summary>
    public async Task<FingerprintPart> ProbeAsync(CancellationToken ct) =>
        FingerprintPart.FromCount(await wfs.GetHitsAsync(ct), "parcel");

    /// <summary>Features per request, lowered to the server's advertised CountDefault.</summary>
    public Task<int> GetPageLimitAsync(CancellationToken ct) => wfs.GetPageLimitAsync(ct);

    /// <summary>A tile's parcels, or null when the page came back full and the tile must be split.</summary>
    public async Task<IReadOnlyList<ParcelFeature>?> GetTileAsync(Tile tile, int pageLimit, CancellationToken ct)
    {
        var page = await wfs.GetPageAsync(tile, pageLimit, WfsGmlParser.ParseCadastralParcels, ct);
        return page.MemberCount >= pageLimit ? null : page.Features;
    }
}
