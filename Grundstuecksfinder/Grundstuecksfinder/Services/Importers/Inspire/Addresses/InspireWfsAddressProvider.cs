using System.Globalization;
using System.Xml.Linq;

namespace Grundstuecksfinder.Services.Importers.Inspire.Addresses;

/// <summary>
/// ad:Address from the state's INSPIRE WFS, fetched per tile right after the tile's parcels
/// (<see cref="AddressSourceType.InspireWfs"/>): the default, and the only way that keeps
/// memory flat whatever the state's size.
/// </summary>
public sealed class InspireWfsAddressProvider(
    WfsFeatureType wfs,
    InspireSourceOptions options) : IAddressProvider
{
    /// <summary>The address count: approximate, like every live service's.</summary>
    public async Task<FingerprintPart> ProbeAsync(CancellationToken ct) =>
        FingerprintPart.FromCount(await wfs.GetHitsAsync(ct), "address");

    public async Task<ITileAddresses> LoadAsync(FingerprintPart probed, CancellationToken ct)
    {
        var expected = await RequireCountAsync(wfs, options, ct);
        var pageLimit = await wfs.GetPageLimitAsync(ct);
        return new PerTile(wfs, options, expected, pageLimit);
    }

    /// <summary>
    /// The total the completeness check measures against, asked again at the start of the fetch:
    /// the probe may have run hours earlier.
    /// </summary>
    internal static async Task<long> RequireCountAsync(WfsFeatureType wfs, InspireSourceOptions options, CancellationToken ct) =>
        await wfs.GetHitsAsync(ct)
        ?? throw new InspireImportException($"{options.Source}: the address service no longer reports a feature count.");

    internal static WfsPage<AddressFeature> Parse(XDocument doc, InspireSourceOptions options) =>
        WfsGmlParser.ParseAddresses(doc, options.IsCityState, options.UsePostNameAsOrt);

    private sealed class PerTile(WfsFeatureType wfs, InspireSourceOptions options, long expected, int pageLimit)
        : ITileAddresses
    {
        public long ExpectedCount => expected;

        public string Description => string.Create(CultureInfo.InvariantCulture, $"per tile, up to {pageLimit} per request");

        public async Task<AddressTile> GetAsync(Tile tile, CancellationToken ct)
        {
            var page = await wfs.GetPageAsync(tile, pageLimit, doc => Parse(doc, options), ct);
            return page.MemberCount >= pageLimit ? AddressTile.Full : AddressTile.Of(page.Features);
        }
    }
}
