namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// The buildings of a source whose parcels name their own addresses (see
/// <see cref="InspireSourceOptions.BuildingFeatureType"/>), by bbox like the parcels: a request
/// that comes back full is reported as such, and the caller splits its area.
/// </summary>
public sealed class InspireBuildingProvider(WfsFeatureType wfs, BuildingFeatureTypeOptions type)
{
    /// <summary>The feature type, for log messages.</summary>
    public string TypeName => wfs.TypeName;

    /// <summary>The buildings in a tile that name an address, or null when the page came back full.</summary>
    public async Task<IReadOnlyList<BuildingFeature>?> GetTileAsync(Tile tile, int pageLimit, CancellationToken ct)
    {
        var page = await wfs.GetPageAsync(tile, pageLimit, doc => WfsGmlParser.ParseBuildings(doc, type), ct);
        return page.MemberCount >= pageLimit ? null : page.Features;
    }
}
