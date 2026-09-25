using System.Globalization;
using System.Xml.Linq;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// One feature type of a WFS 2.0 download service (cp:CadastralParcel, ad:Address,
/// ave:Flurstueck) and the few requests the import makes of it: its feature count, its page-size
/// limit, and GetFeature pages by bbox or by startIndex. Every response is checked for being a
/// complete feature collection in the source's CRS before it is used. A typeNamespace binds the
/// type name's prefix via NAMESPACES on every request, for servers that don't resolve the prefix
/// on their own; null sends none.
/// </summary>
public sealed partial class WfsFeatureType(
    InspireServiceClient client,
    InspireSourceOptions options,
    ILogger logger,
    string baseUrl,
    string typeName,
    string? typeNamespace = null)
{
    public const string AddressType = "ad:Address";

    public string TypeName => typeName;

    /// <summary>The type name as a query parameter, with its NAMESPACES binding if one is configured.</summary>
    private string TypeParameters
    {
        get
        {
            var typeNames = $"&typenames={Uri.EscapeDataString(typeName)}";
            var colon = typeName.IndexOf(':', StringComparison.Ordinal);
            return typeNamespace is null || colon <= 0
                ? typeNames
                : $"{typeNames}&namespaces=xmlns({Uri.EscapeDataString(typeName[..colon])},{Uri.EscapeDataString(typeNamespace)})";
        }
    }

    /// <summary>numberMatched of a resultType=hits request; null if the server doesn't say.</summary>
    public Task<long?> GetHitsAsync(CancellationToken ct)
    {
        var url = FormattableString.Invariant(
            $"{baseUrl}?service=WFS&version=2.0.0&request=GetFeature{TypeParameters}&resultType=hits");
        return client.ExecuteAsync(async token =>
        {
            var doc = await client.ReadXmlAsync(url, token);
            EnsureCompleteFeatureCollection(doc, url);
            var attr = doc.Root!.Attributes().FirstOrDefault(a => a.Name.LocalName == "numberMatched")?.Value;
            return long.TryParse(attr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : (long?)null;
        }, typeName, "hits", ct);
    }

    /// <summary>
    /// Features per request: <see cref="InspireSourceOptions.PageSize"/>, lowered to the server's
    /// advertised CountDefault — a server silently capping below what was asked would otherwise
    /// make full tiles look complete.
    /// </summary>
    public async Task<int> GetPageLimitAsync(CancellationToken ct)
    {
        var url = $"{baseUrl}?service=WFS&version=2.0.0&request=GetCapabilities";
        try
        {
            var serverLimit = await client.ExecuteAsync(
                async token => ParseCountDefault(await client.ReadXmlAsync(url, token)), "GetCapabilities", baseUrl, ct);

            return serverLimit is > 0 && serverLimit < options.PageSize ? (int)serverLimit : options.PageSize;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The completeness check still catches a server capping pages below PageSize.
            LogCapabilitiesFailed(logger, ex, options.Source, baseUrl);
            return options.PageSize;
        }
    }

    public static long? ParseCountDefault(XDocument capabilities)
    {
        var constraint = capabilities.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Constraint" &&
                                 (string?)e.Attribute("name") == "CountDefault");
        var value = constraint?.Descendants()
            .FirstOrDefault(e => e.Name.LocalName is "DefaultValue" or "Value")?.Value;
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) ? limit : null;
    }

    /// <summary>The features in a tile (the bbox filter is closed on every edge).</summary>
    public Task<WfsPage<T>> GetPageAsync<T>(Tile tile, int count, Func<XDocument, WfsPage<T>> parse, CancellationToken ct)
    {
        var crs = Uri.EscapeDataString(options.Crs);
        return GetPageAsync(FormattableString.Invariant($"&bbox={tile.Bbox},{crs}"), tile.Bbox, count, parse, ct);
    }

    /// <summary>
    /// One page of the whole feature type, for a service whose bbox filter is unusable. Only
    /// safe where a page is the same across requests and adjacent pages don't overlap.
    /// </summary>
    public Task<WfsPage<T>> GetPageAsync<T>(long startIndex, int count, Func<XDocument, WfsPage<T>> parse, CancellationToken ct) =>
        GetPageAsync(FormattableString.Invariant($"&startIndex={startIndex}"),
            FormattableString.Invariant($"startIndex={startIndex}"), count, parse, ct);

    private async Task<WfsPage<T>> GetPageAsync<T>(
        string filter, string where, int count, Func<XDocument, WfsPage<T>> parse, CancellationToken ct)
    {
        var resolveSuffix = typeName == AddressType ? "&resolve=local&resolvedepth=2" : "";
        // srsName ensures the response uses the configured CRS even when the server's
        // default differs (e.g. Hessen defaults to EPSG:4258 but supports 25832).
        var crs = Uri.EscapeDataString(options.Crs);
        var url = FormattableString.Invariant(
            $"{baseUrl}?service=WFS&version=2.0.0&request=GetFeature{TypeParameters}{filter}&srsName={crs}&count={count}{resolveSuffix}");

        var page = await client.ExecuteAsync(async token =>
        {
            var doc = await client.ReadXmlAsync(url, token);
            EnsureCompleteFeatureCollection(doc, url);
            return parse(doc);
        }, typeName, where, ct);

        // Unrecognised srsNames count as foreign too: an unchecked CRS could silently misjoin.
        var expectedEpsg = options.CrsEpsgCode;
        var foreign = page.SrsNames.Where(name => InspireSourceOptions.ParseEpsgCode(name) != expectedEpsg).ToList();
        if (foreign.Count > 0)
            throw new InspireImportException(
                $"{options.Source}: {typeName} answered in {string.Join(", ", foreign)} instead of EPSG:{expectedEpsg}.");

        return page;
    }

    /// <summary>
    /// Servers answer errors (overload, internal exceptions) with an ows:ExceptionReport or a
    /// truncated collection, often with HTTP 200. Parsed as-is that would be an empty tile.
    /// </summary>
    private static void EnsureCompleteFeatureCollection(XDocument doc, string url)
    {
        var root = doc.Root!;
        if (root.Name.LocalName != "FeatureCollection")
            throw new WfsResponseException($"Expected a FeatureCollection but got {root.Name.LocalName} from {url}: {Truncate(root.Value)}");
        if (root.Elements().Any(e => e.Name.LocalName == "truncatedResponse"))
            throw new WfsResponseException($"The server truncated its response to {url}.");
    }

    private static string Truncate(string text) => text.Length <= 300 ? text.Trim() : text[..300].Trim() + "…";

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: couldn't read the page-size limit from {Url}'s capabilities; using the configured PageSize")]
    private static partial void LogCapabilitiesFailed(ILogger logger, Exception exception, string source, string url);
}
