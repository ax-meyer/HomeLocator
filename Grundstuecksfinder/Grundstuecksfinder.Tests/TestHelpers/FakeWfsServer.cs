using System.Globalization;
using System.Net;
using System.Security;
using System.Text;

namespace Grundstuecksfinder.Tests.TestHelpers;

/// <summary>A square-ish parcel for <see cref="FakeWfsServer"/>, in the server's CRS.</summary>
public sealed record FakeParcel(string Id, double MinX, double MinY, double MaxX, double MaxY, double AreaM2);

/// <summary>An address point for <see cref="FakeWfsServer"/>, in the server's CRS; Plz null publishes none.</summary>
public sealed record FakeAddress(string Id, double X, double Y, string Street, string Hnr, string? Plz = null);

/// <summary>
/// In-memory stand-in for a state's parcel + address INSPIRE WFS pair. Answers GetCapabilities,
/// resultType=hits and bbox GetFeature requests from its parcel/address lists the way real
/// servers do, including their quirks: numberReturned/numberMatched streamed as "0"/"unknown"
/// (HE, BW), a changing timeStamp per response, and optionally a silent page-size cap.
/// </summary>
public sealed class FakeWfsServer : HttpMessageHandler
{
    public const string ParcelUrl = "http://fake/parcels";
    public const string AddressUrl = "http://fake/addresses";

    public List<FakeParcel> Parcels { get; } = [];
    public List<FakeAddress> Addresses { get; } = [];

    /// <summary>Returns at most this many features per GetFeature, whatever count was asked for.</summary>
    public int? ServerCap { get; set; }

    /// <summary>Answers any bbox request on the address service with Hamburg's mixed-SRID error.</summary>
    public bool FailAddressBbox { get; set; }

    /// <summary>Answers every paged address request with the first page, as a server that silently ignores startIndex would.</summary>
    public bool IgnoreStartIndex { get; set; }

    /// <summary>Answers every OGC API Features address request with the first page, as a server that silently ignores offset would.</summary>
    public bool IgnoreOffset { get; set; }

    /// <summary>CountDefault advertised in GetCapabilities; null advertises none.</summary>
    public int? AdvertisedCountDefault { get; set; }

    /// <summary>srsName written on every geometry.</summary>
    public string ResponseSrsName { get; set; } = "urn:ogc:def:crs:EPSG::25832";

    /// <summary>numberMatched for resultType=hits on the address service; null reports the real count.</summary>
    public string? AddressHitsOverride { get; set; }

    /// <summary>
    /// Called before each request is answered, with its 0-based index among all requests so far;
    /// return a response (or throw) to override the normal answer, or null to answer normally.
    /// </summary>
    public Func<Uri, int, HttpResponseMessage?>? Interceptor { get; set; }

    public List<Uri> Requests { get; } = [];

    public IEnumerable<Uri> GetFeatureRequests(string baseUrl) =>
        Requests.Where(u => u.ToString().StartsWith(baseUrl, StringComparison.Ordinal)
                            && Query(u).GetValueOrDefault("request") == "GetFeature"
                            && !Query(u).ContainsKey("resultType"));

    /// <summary>OGC API Features "items" requests (limit/offset, no WFS "request" parameter).</summary>
    public IEnumerable<Uri> OgcApiRequests(string baseUrl) =>
        Requests.Where(u => u.ToString().StartsWith(baseUrl, StringComparison.Ordinal) && Query(u).ContainsKey("limit"));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var index = Requests.Count;
        Requests.Add(uri);

        var intercepted = Interceptor?.Invoke(uri, index);
        return Task.FromResult(intercepted ?? Answer(uri));
    }

    private HttpResponseMessage Answer(Uri uri)
    {
        var query = Query(uri);
        var isParcels = uri.ToString().StartsWith(ParcelUrl, StringComparison.Ordinal);

        if (!isParcels && query.ContainsKey("limit") && !query.ContainsKey("request"))
            return OgcApiAddressResponse(query);

        if (query.GetValueOrDefault("request") == "GetCapabilities")
            return Xml(Capabilities());

        if (query.GetValueOrDefault("resultType") == "hits")
        {
            var hits = isParcels
                ? Parcels.Count.ToString(CultureInfo.InvariantCulture)
                : AddressHitsOverride ?? Addresses.Count.ToString(CultureInfo.InvariantCulture);
            return Xml($"""<wfs:FeatureCollection xmlns:wfs="http://www.opengis.net/wfs/2.0" numberMatched="{hits}" numberReturned="0"/>""");
        }

        var count = int.Parse(query["count"], CultureInfo.InvariantCulture);
        var limit = Math.Min(count, ServerCap ?? int.MaxValue);

        // Hamburg's address service: its geometries carry SRID 0, so any bbox fails server-side
        // and the importer pages with startIndex instead.
        if (!isParcels && FailAddressBbox && query.ContainsKey("bbox"))
            return Xml("""
                <ows:ExceptionReport xmlns:ows="http://www.opengis.net/ows/1.1"><ows:Exception>
                  <ows:ExceptionText>ST_Intersects: Operation on mixed SRID geometries (Point, 0) != (Polygon, 25832)</ows:ExceptionText>
                </ows:Exception></ows:ExceptionReport>
                """);

        if (!isParcels && !query.ContainsKey("bbox"))
        {
            var startIndex = !IgnoreStartIndex && query.TryGetValue("startIndex", out var raw)
                ? int.Parse(raw, CultureInfo.InvariantCulture)
                : 0;
            return Xml(AddressCollection(Addresses
                .OrderBy(a => a.Id, StringComparer.Ordinal).Skip(startIndex).Take(limit)));
        }

        var bbox = query["bbox"].Split(',').Take(4)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();

        return isParcels
            ? Xml(ParcelCollection(Parcels
                .Where(p => p.MinX <= bbox[2] && p.MaxX >= bbox[0] && p.MinY <= bbox[3] && p.MaxY >= bbox[1])
                .OrderBy(p => p.Id, StringComparer.Ordinal).Take(limit)))
            : Xml(AddressCollection(Addresses
                .Where(a => a.X >= bbox[0] && a.X <= bbox[2] && a.Y >= bbox[1] && a.Y <= bbox[3])
                .OrderBy(a => a.Id, StringComparer.Ordinal).Take(limit)));
    }

    private string Capabilities()
    {
        var constraint = AdvertisedCountDefault is { } cap
            ? $"""<ows:Constraint name="CountDefault"><ows:NoValues/><ows:DefaultValue>{cap}</ows:DefaultValue></ows:Constraint>"""
            : "";
        return $"""
            <wfs:WFS_Capabilities xmlns:wfs="http://www.opengis.net/wfs/2.0" xmlns:ows="http://www.opengis.net/ows/1.1">
              <ows:OperationsMetadata>{constraint}</ows:OperationsMetadata>
            </wfs:WFS_Capabilities>
            """;
    }

    private static string CollectionStart(string defaultNs) =>
        $"""<wfs:FeatureCollection xmlns:wfs="http://www.opengis.net/wfs/2.0" xmlns:gml="http://www.opengis.net/gml/3.2" xmlns:xlink="http://www.w3.org/1999/xlink" xmlns:gn="http://inspire.ec.europa.eu/schemas/gn/4.0" xmlns="{defaultNs}" timeStamp="{DateTimeOffset.UtcNow:O}" numberMatched="unknown" numberReturned="0">""";

    private string ParcelCollection(IEnumerable<FakeParcel> parcels)
    {
        var sb = new StringBuilder(CollectionStart("http://inspire.ec.europa.eu/schemas/cp/4.0"));
        foreach (var p in parcels)
        {
            var ring = string.Join(' ', new[]
            {
                (p.MinX, p.MinY), (p.MaxX, p.MinY), (p.MaxX, p.MaxY), (p.MinX, p.MaxY), (p.MinX, p.MinY),
            }.Select(c => FormattableString.Invariant($"{c.Item1} {c.Item2}")));
            sb.Append(CultureInfo.InvariantCulture, $"""
                <wfs:member><CadastralParcel gml:id="{p.Id}">
                  <areaValue uom="m2">{p.AreaM2.ToString(CultureInfo.InvariantCulture)}</areaValue>
                  <geometry><gml:Polygon gml:id="{p.Id}_g" srsName="{ResponseSrsName}"><gml:exterior><gml:LinearRing>
                    <gml:posList>{ring}</gml:posList>
                  </gml:LinearRing></gml:exterior></gml:Polygon></geometry>
                </CadastralParcel></wfs:member>
                """);
        }
        return sb.Append("</wfs:FeatureCollection>").ToString();
    }

    private string AddressCollection(IEnumerable<FakeAddress> addresses)
    {
        var list = addresses.ToList();
        var sb = new StringBuilder(CollectionStart("http://inspire.ec.europa.eu/schemas/ad/4.0"));
        foreach (var a in list)
        {
            sb.Append(CultureInfo.InvariantCulture, $"""
                <wfs:member><Address gml:id="{a.Id}">
                  <position><GeographicPosition><geometry>
                    <gml:Point gml:id="{a.Id}_p" srsName="{ResponseSrsName}"><gml:pos>{a.X.ToString(CultureInfo.InvariantCulture)} {a.Y.ToString(CultureInfo.InvariantCulture)}</gml:pos></gml:Point>
                  </geometry></GeographicPosition></position>
                  <locator><AddressLocator><designator><LocatorDesignator>
                    <designator>{a.Hnr}</designator>
                    <type xlink:href="http://inspire.ec.europa.eu/codelist/LocatorDesignatorTypeValue/addressNumber"/>
                  </LocatorDesignator></designator></AddressLocator></locator>
                  <component xlink:href="#TN_{a.Id}"/>{(a.Plz is null ? "" : $"<component xlink:href=\"#PD_{a.Id}\"/>")}
                  <component xlink:href="#AU_gemeinde"/>
                </Address></wfs:member>
                """);
        }

        sb.Append("<wfs:additionalObjects><wfs:SimpleFeatureCollection>");
        foreach (var a in list)
        {
            sb.Append(CultureInfo.InvariantCulture, $"""<wfs:member><ThoroughfareName gml:id="TN_{a.Id}">{Name(a.Street)}</ThoroughfareName></wfs:member>""");
            if (a.Plz is not null)
                sb.Append(CultureInfo.InvariantCulture, $"""<wfs:member><PostalDescriptor gml:id="PD_{a.Id}"><postCode>{a.Plz}</postCode></PostalDescriptor></wfs:member>""");
        }
        sb.Append(CultureInfo.InvariantCulture, $"""<wfs:member><AdminUnitName gml:id="AU_gemeinde"><alternativeIdentifier>01060099</alternativeIdentifier>{Name("Testgemeinde")}</AdminUnitName></wfs:member>""");
        return sb.Append("</wfs:SimpleFeatureCollection></wfs:additionalObjects></wfs:FeatureCollection>").ToString();
    }

    /// <summary>
    /// Answers Saarland's OGC API Features shape: limit/offset paging, GeoJSON with the
    /// ALKIS-native properties <see cref="Grundstuecksfinder.Services.Importers.Inspire.OgcApiAddressParser"/> reads.
    /// </summary>
    private static readonly double[] ZeroCoordinates = [0.0, 0.0];

    private HttpResponseMessage OgcApiAddressResponse(Dictionary<string, string> query)
    {
        var limit = int.Parse(query["limit"], CultureInfo.InvariantCulture);
        var count = Math.Min(limit, ServerCap ?? int.MaxValue);
        var offset = !IgnoreOffset && query.TryGetValue("offset", out var raw)
            ? int.Parse(raw, CultureInfo.InvariantCulture)
            : 0;
        var page = Addresses.OrderBy(a => a.Id, StringComparer.Ordinal).Skip(offset).Take(count).ToList();
        var numberMatched = AddressHitsOverride ?? Addresses.Count.ToString(CultureInfo.InvariantCulture);

        var features = page.Select(a => new
        {
            type = "Feature",
            properties = new
            {
                gml_id = a.Id,
                STN = a.Street,
                HNR = a.Hnr,
                ADZ = (string?)null,
                PLZ = a.Plz,
                ONM = "Testgemeinde",
                XCOORD = a.X + 32_000_000,
                YCOORD = a.Y,
            },
            geometry = new { type = "Point", coordinates = ZeroCoordinates },
        });
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "FeatureCollection",
            numberMatched = long.Parse(numberMatched, CultureInfo.InvariantCulture),
            numberReturned = page.Count,
            features,
        });
        return Json(body);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/geo+json"),
    };

    private static string Name(string text) =>
        $"<name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>{SecurityElement.Escape(text)}</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name>";

    private static HttpResponseMessage Xml(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/xml"),
    };

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(kv => kv[0], kv => kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "");
}
