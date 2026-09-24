using System.Text.Json;

namespace Grundstuecksfinder.Services.Importers.Inspire.Addresses;

/// <summary>
/// Addresses from an OGC API Features "items" endpoint carrying the ALKIS-native Hauskoordinaten
/// schema (<see cref="AddressSourceType.OgcApiFeatures"/>), paged with limit/offset and
/// preloaded, for a state whose INSPIRE address WFS is usable but far too slow (Saarland: ~5
/// addr/s at any tile size vs ~800 addr/s here). See <see cref="OgcApiAddressParser"/>.
/// </summary>
public sealed partial class OgcApiFeaturesAddressProvider(
    InspireServiceClient client,
    InspireSourceOptions options,
    ILogger logger) : IAddressProvider
{
    private const string What = "OGC API addresses";
    private const string GeoJson = "application/geo+json";

    private string Url => options.AddressSource.Url;

    /// <summary>numberMatched, via the smallest page: approximate, like every live service's count.</summary>
    public async Task<FingerprintPart> ProbeAsync(CancellationToken ct) =>
        FingerprintPart.FromCount(await GetNumberMatchedAsync(ct), "address");

    public async Task<ITileAddresses> LoadAsync(CancellationToken ct)
    {
        var expected = await GetNumberMatchedAsync(ct)
            ?? throw new InspireImportException($"{options.Source}: the address service no longer reports a feature count.");
        var pageSize = options.AddressSource.OgcApiPageSize;

        var all = new PreloadedAddresses.Builder();
        // A server that silently ignores offset would hand out its first page forever.
        var limit = expected + pageSize;
        for (var offset = 0L; ; offset += pageSize)
        {
            var url = FormattableString.Invariant($"{Url}?limit={pageSize}&offset={offset}");
            var page = await client.ExecuteAsync(async token =>
            {
                using var doc = await client.ReadJsonAsync(url, GeoJson, token);
                return OgcApiAddressParser.ParseAddresses(doc);
            }, What, FormattableString.Invariant($"offset={offset}"), ct);
            foreach (var address in page.Features)
                all.Add(address);

            if (all.Count > limit)
                throw new InspireImportException(FormattableString.Invariant(
                    $"{options.Source}: paging the address service passed {all.Count} addresses although it reports {expected}; is offset ignored?"));
            if (page.MemberCount < pageSize) break;
        }

        LogPagedAddresses(logger, options.Source, all.Count, expected);
        return all.Build(expected);
    }

    private Task<long?> GetNumberMatchedAsync(CancellationToken ct)
    {
        var url = $"{Url}?limit=1";
        return client.ExecuteAsync(async token =>
        {
            using var doc = await client.ReadJsonAsync(url, GeoJson, token);
            return doc.RootElement.TryGetProperty("numberMatched", out var value) && value.ValueKind == JsonValueKind.Number
                   && value.TryGetInt64(out var matched)
                ? matched : (long?)null;
        }, What, "hits", ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: paged {Fetched} of {Expected} addresses in one pass, before joining them to parcels")]
    private static partial void LogPagedAddresses(ILogger logger, string source, int fetched, long expected);
}
