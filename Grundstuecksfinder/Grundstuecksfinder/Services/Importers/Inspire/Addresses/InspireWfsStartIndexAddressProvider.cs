namespace Grundstuecksfinder.Services.Importers.Inspire.Addresses;

/// <summary>
/// ad:Address from the state's INSPIRE WFS, fetched in one pass by startIndex and preloaded
/// (<see cref="AddressSourceType.InspireWfsStartIndex"/>), for a service whose bbox filter is
/// unusable: Hamburg's address geometries carry SRID 0, so the server rejects every bbox as
/// "mixed SRID geometries". Only for services whose paging is known to be stable — a page must
/// be the same across requests and adjacent pages must not overlap, or addresses are silently
/// lost. Verified for Hamburg before enabling it there.
/// </summary>
public sealed partial class InspireWfsStartIndexAddressProvider(
    WfsFeatureType wfs,
    InspireSourceOptions options,
    ILogger logger,
    NameCatalogLoader? nameCatalogLoader) : IAddressProvider
{
    public async Task<FingerprintPart> ProbeAsync(CancellationToken ct) =>
        FingerprintPart.FromCount(await wfs.GetHitsAsync(ct), "address");

    public async Task<ITileAddresses> LoadAsync(CancellationToken ct)
    {
        var expected = await InspireWfsAddressProvider.RequireCountAsync(wfs, options, ct);
        var pageSize = await wfs.GetPageLimitAsync(ct);
        var nameCatalog = await InspireWfsAddressProvider.LoadNameCatalogAsync(options, nameCatalogLoader, ct);

        var all = new PreloadedAddresses.Builder();
        // A server that silently ignores startIndex would hand out its first page forever.
        var limit = expected + pageSize;
        for (var startIndex = 0L; ; startIndex += pageSize)
        {
            var page = await wfs.GetPageAsync(startIndex, pageSize,
                doc => InspireWfsAddressProvider.Parse(doc, options, nameCatalog), ct);
            foreach (var address in page.Features)
                all.Add(address);

            if (all.Count > limit)
                throw new InspireImportException(FormattableString.Invariant(
                    $"{options.Source}: paging the address service passed {all.Count} addresses although it reports {expected}; is startIndex ignored?"));
            if (page.MemberCount < pageSize) break;
        }

        LogPagedAddresses(logger, options.Source, all.Count, expected);
        InspireWfsAddressProvider.ReportNameRepairs(logger, options.Source, nameCatalog);
        return all.Build(expected);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: paged {Fetched} of {Expected} addresses in one pass, before joining them to parcels")]
    private static partial void LogPagedAddresses(ILogger logger, string source, int fetched, long expected);
}
