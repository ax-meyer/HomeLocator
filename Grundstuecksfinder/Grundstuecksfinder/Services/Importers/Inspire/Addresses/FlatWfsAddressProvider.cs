using System.Globalization;
using System.Xml.Linq;

namespace Grundstuecksfinder.Services.Importers.Inspire.Addresses;

/// <summary>
/// Addresses from a WFS whose features are flat — one element per field, a point geometry —
/// fetched per tile right after the tile's parcels (<see cref="AddressSourceType.FlatWfs"/>):
/// for states whose address dataset isn't INSPIRE ad:Address but carries what the import
/// needs, PLZ included (Bremen's native ALKIS Gebäudeadressen).
/// </summary>
public sealed class FlatWfsAddressProvider(
    WfsFeatureType wfs,
    InspireSourceOptions options) : IAddressProvider
{
    /// <summary>The address count: approximate, like every live service's.</summary>
    public async Task<FingerprintPart> ProbeAsync(CancellationToken ct) =>
        FingerprintPart.FromCount(await wfs.GetHitsAsync(ct), "address");

    public async Task<ITileAddresses> LoadAsync(FingerprintPart probed, CancellationToken ct)
    {
        var expected = await InspireWfsAddressProvider.RequireCountAsync(wfs, options, ct);
        var pageLimit = await wfs.GetPageLimitAsync(ct);
        return new PerTile(wfs, options.AddressSource, expected, pageLimit);
    }

    private sealed class PerTile(WfsFeatureType wfs, AddressSourceOptions source, long expected, int pageLimit)
        : ITileAddresses
    {
        private readonly string _localName = source.TypeName![(source.TypeName!.IndexOf(':', StringComparison.Ordinal) + 1)..];

        public long ExpectedCount => expected;

        public string Description => string.Create(CultureInfo.InvariantCulture, $"{wfs.TypeName} per tile, up to {pageLimit} per request");

        public async Task<AddressTile> GetAsync(Tile tile, CancellationToken ct)
        {
            var page = await wfs.GetPageAsync(tile, pageLimit, Parse, ct);
            return page.MemberCount >= pageLimit ? AddressTile.Full : AddressTile.Of(page.Features);
        }

        private WfsPage<AddressFeature> Parse(XDocument doc) => WfsGmlParser.ParseFlatAddresses(doc, _localName, source.Fields!);
    }
}
