using System.Globalization;
using System.Xml.Linq;

namespace Grundstuecksfinder.Services.Importers.Inspire.Addresses;

/// <summary>
/// ad:Address from the state's INSPIRE WFS, fetched per tile right after the tile's parcels
/// (<see cref="AddressSourceType.InspireWfs"/>): the default, and the only way that keeps
/// memory flat whatever the state's size.
/// </summary>
public sealed partial class InspireWfsAddressProvider(
    WfsFeatureType wfs,
    InspireSourceOptions options,
    ILogger logger,
    NameCatalogLoader? nameCatalogLoader) : IAddressProvider
{
    /// <summary>The address count: approximate, like every live service's.</summary>
    public async Task<FingerprintPart> ProbeAsync(CancellationToken ct) =>
        FingerprintPart.FromCount(await wfs.GetHitsAsync(ct), "address");

    public async Task<ITileAddresses> LoadAsync(FingerprintPart probed, CancellationToken ct)
    {
        var expected = await RequireCountAsync(wfs, options, ct);
        var pageLimit = await wfs.GetPageLimitAsync(ct);
        var nameCatalog = await LoadNameCatalogAsync(options, nameCatalogLoader, ct);
        return new PerTile(wfs, options, logger, expected, pageLimit, nameCatalog);
    }

    /// <summary>
    /// The total the completeness check measures against, asked again at the start of the fetch:
    /// the probe may have run hours earlier.
    /// </summary>
    internal static async Task<long> RequireCountAsync(WfsFeatureType wfs, InspireSourceOptions options, CancellationToken ct) =>
        await wfs.GetHitsAsync(ct)
        ?? throw new InspireImportException($"{options.Source}: the address service no longer reports a feature count.");

    /// <summary>Hessen's intact spellings, loaded before the (long) fetch; null elsewhere.</summary>
    internal static async Task<NameCatalog?> LoadNameCatalogAsync(
        InspireSourceOptions options, NameCatalogLoader? loader, CancellationToken ct) =>
        options.NameCatalog.IsConfigured && loader is not null
            ? await loader.LoadAsync(options.NameCatalog, ct)
            : null;

    internal static WfsPage<AddressFeature> Parse(XDocument doc, InspireSourceOptions options, NameCatalog? nameCatalog) =>
        WfsGmlParser.ParseAddresses(doc, options.IsCityState, options.UsePostNameAsOrt, nameCatalog);

    internal static void ReportNameRepairs(ILogger logger, string source, NameCatalog? nameCatalog)
    {
        if (nameCatalog is not null)
            LogNameRepairs(logger, source, nameCatalog.Repaired, nameCatalog.Unrepairable);
    }

    private sealed class PerTile(
        WfsFeatureType wfs, InspireSourceOptions options, ILogger logger, long expected, int pageLimit, NameCatalog? nameCatalog)
        : ITileAddresses
    {
        public long ExpectedCount => expected;

        public string Description => string.Create(CultureInfo.InvariantCulture, $"per tile, up to {pageLimit} per request");

        public async Task<AddressTile> GetAsync(Tile tile, CancellationToken ct)
        {
            var page = await wfs.GetPageAsync(tile, pageLimit, doc => Parse(doc, options, nameCatalog), ct);
            return page.MemberCount >= pageLimit ? AddressTile.Full : AddressTile.Of(page.Features);
        }

        public void ReportSummary() => ReportNameRepairs(logger, options.Source, nameCatalog);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: repaired {Repaired} damaged names from the catalogue; {Unrepairable} kept as published")]
    private static partial void LogNameRepairs(ILogger logger, string source, int repaired, int unrepairable);
}
