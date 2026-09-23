using System.Globalization;
using System.Xml.Linq;
using NetTopologySuite.Geometries;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>A parsed parcel: its official area and its (possibly multi-part) geometry.</summary>
public sealed record ParcelFeature(Geometry Geometry, double AreaM2);

/// <summary>
/// One parsed GetFeature response. <see cref="MemberCount"/> counts every returned feature,
/// including ones the parser skipped as malformed, so "the page is full" can be judged
/// against the requested count. <see cref="SrsNames"/> are the CRSs the geometries claim.
/// </summary>
public sealed record WfsPage<T>(IReadOnlyList<T> Features, int MemberCount, IReadOnlySet<string> SrsNames);

/// <summary>
/// Parses cp:CadastralParcel and ad:Address WFS GetFeature GML responses. Matches elements by
/// local name only (not full XName), so it isn't pinned to one INSPIRE schema version/prefix
/// combination across states. Skips individual malformed features rather than failing the
/// whole page, mirroring <see cref="DelimitedPropertyParser"/>'s null-tolerant style.
/// </summary>
public static class WfsGmlParser
{
    private static readonly GeometryFactory GeometryFactory = new();

    public static WfsPage<ParcelFeature> ParseCadastralParcels(Stream gml) => ParseCadastralParcels(XDocument.Load(gml));

    public static WfsPage<ParcelFeature> ParseCadastralParcels(XDocument doc)
    {
        var members = TopLevelMembers(doc);
        var parcels = new List<ParcelFeature>(members.Count);
        foreach (var member in members)
        {
            var parcel = member.Elements().FirstOrDefault(e => e.Name.LocalName == "CadastralParcel");
            if (parcel is null) continue;

            var areaText = parcel.Elements().FirstOrDefault(e => e.Name.LocalName == "areaValue")?.Value;
            if (!double.TryParse(areaText, NumberStyles.Any, CultureInfo.InvariantCulture, out var area))
                continue;

            var geometry = ParseParcelGeometry(parcel);
            if (geometry is null) continue;

            parcels.Add(new ParcelFeature(geometry, area));
        }
        return new WfsPage<ParcelFeature>(parcels, members.Count, SrsNames(doc));
    }

    public static WfsPage<AddressFeature> ParseAddresses(Stream gml, bool isCityState = false, bool usePostName = true,
        NameCatalog? nameCatalog = null) =>
        ParseAddresses(XDocument.Load(gml), isCityState, usePostName, nameCatalog);

    /// <summary>
    /// With <c>usePostName</c> false, the PostalDescriptor's postName is ignored as an Ort
    /// candidate, for sources whose postName is one arbitrary place per postcode rather than
    /// the address's own town.
    /// </summary>
    public static WfsPage<AddressFeature> ParseAddresses(XDocument doc, bool isCityState = false, bool usePostName = true,
        NameCatalog? nameCatalog = null)
    {
        var componentsById = new Dictionary<string, XElement>();
        foreach (var member in doc.Root!
                     .Descendants().Where(e => e.Name.LocalName == "additionalObjects")
                     .Descendants().Where(e => e.Name.LocalName == "member"))
        {
            foreach (var component in member.Elements())
                componentsById[GmlId(component)] = component;
        }

        var members = TopLevelMembers(doc);
        var addresses = new List<AddressFeature>(members.Count);
        foreach (var member in members)
        {
            var address = member.Elements().FirstOrDefault(e => e.Name.LocalName == "Address");
            if (address is null) continue;

            var point = ParsePoint(address.Descendants().FirstOrDefault(e => e.Name.LocalName == "Point"));
            if (point is null) continue; // nothing to join without a location

            var (hnr, hnrZus) = ParseDesignators(address);
            var (str, plz, ort, gemeinde) = ResolveComponents(address, componentsById, isCityState, usePostName, nameCatalog);

            addresses.Add(new AddressFeature(point, str, hnr, hnrZus, plz, ort, gemeinde));
        }
        return new WfsPage<AddressFeature>(addresses, members.Count, SrsNames(doc));
    }

    private static List<XElement> TopLevelMembers(XDocument doc) =>
        doc.Root!.Elements().Where(e => e.Name.LocalName == "member").ToList();

    /// <summary>Every distinct srsName in the document (geometries and envelopes).</summary>
    private static HashSet<string> SrsNames(XDocument doc) =>
        doc.Descendants()
            .Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "srsName")?.Value)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// A parcel's geometry: gml:Polygon (SH, BW, BB) or gml:Surface > gml:PolygonPatch (HH), and
    /// for multi-part parcels (gml:MultiSurface) all of its parts, so an address in any part matches.
    /// </summary>
    private static Geometry? ParseParcelGeometry(XElement parcel)
    {
        var polygons = parcel.Descendants()
            .Where(e => e.Name.LocalName is "Polygon" or "PolygonPatch")
            .Select(ParsePolygon)
            .OfType<Polygon>()
            .ToArray();
        return polygons.Length switch
        {
            0 => null,
            1 => polygons[0],
            _ => GeometryFactory.CreateMultiPolygon(polygons),
        };
    }

    private static (string? Str, string? Plz, string? Ort, string? Gemeinde) ResolveComponents(
        XElement address, Dictionary<string, XElement> componentsById, bool isCityState, bool usePostName,
        NameCatalog? nameCatalog)
    {
        string? str = null;
        string? plz = null;
        string? postName = null;
        var rawUnits = new List<(string? Ags, string? Level, string Name)>();

        var refIds = address.Elements().Where(e => e.Name.LocalName == "component")
            .Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value)
            .Where(href => href is not null)
            .Select(href => href!.TrimStart('#'));

        foreach (var id in refIds)
        {
            if (!componentsById.TryGetValue(id, out var component)) continue;

            switch (component.Name.LocalName)
            {
                case "ThoroughfareName":
                    str ??= CatalogueName(nameCatalog, id, FirstText(component));
                    break;
                case "PostalDescriptor":
                    plz ??= PostalCode.Normalize(component.Elements().FirstOrDefault(e => e.Name.LocalName == "postCode")?.Value);
                    if (usePostName) postName ??= FirstText(component);
                    break;
                case "AdminUnitName":
                    var name = CatalogueName(nameCatalog, id, FirstText(component));
                    if (name is null) break;
                    var ags = component.Elements().FirstOrDefault(e => e.Name.LocalName == "alternativeIdentifier")?.Value;
                    rawUnits.Add((string.IsNullOrWhiteSpace(ags) ? null : ags.Trim(), LevelOrdinal(component), name));
                    break;
            }
        }

        // SH/SN/BB carry AGS codes, whose length gives the admin level directly. HE/BW/HH only
        // carry ad:level, which is mapped to a synthetic AGS length — differently for
        // city-states (configured per source), where the Land itself is the Gemeinde.
        var adminUnits = rawUnits
            .Select(u => (AgsLength: u.Ags?.Length ?? SyntheticAgsLength(u.Level, isCityState), u.Name))
            .Where(u => u.AgsLength > 0)
            .ToList();

        // German AGS codes nest by length: 2=Land, 3=Regierungsbezirk, 5=Kreis, 8=Gemeinde,
        // >8=Ortsteil. Gemeinde is the Gemeinde-level unit only — never a Kreis or the Land.
        // Ort prefers an Ortsteil, then the postal town name (what addresses and Nominatim
        // use), then the Gemeinde.
        var gemeindeUnit = adminUnits.Where(u => u.AgsLength == 8).Select(u => u.Name).FirstOrDefault();
        var ortsteil = adminUnits.Where(u => u.AgsLength > 8).OrderByDescending(u => u.AgsLength)
            .Select(u => u.Name).FirstOrDefault();

        var gemeinde = PlaceNameNormalizer.NormalizePlace(gemeindeUnit);
        var ort = PlaceNameNormalizer.NormalizePlace(ortsteil)
                  ?? PlaceNameNormalizer.NormalizePlace(postName)
                  ?? gemeinde;

        return (PlaceNameNormalizer.RepairStreet(str), plz, ort, gemeinde);
    }

    /// <summary>
    /// The catalogue's spelling where the source lost characters to U+FFFD, otherwise what the
    /// source published (see <see cref="NameCatalog"/>).
    /// </summary>
    private static string? CatalogueName(NameCatalog? catalog, string componentId, string? published) =>
        published is null || catalog is null ? published : catalog.Repair(componentId, published) ?? published;

    private static (string? Hnr, string? HnrZus) ParseDesignators(XElement address)
    {
        string? hnr = null;
        string? hnrZus = null;
        foreach (var designator in address.Descendants().Where(e => e.Name.LocalName == "LocatorDesignator"))
        {
            var type = designator.Elements().FirstOrDefault(e => e.Name.LocalName == "type")
                ?.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value ?? "";
            var value = designator.Elements().FirstOrDefault(e => e.Name.LocalName == "designator")?.Value;
            if (value is null) continue;

            if (type.EndsWith("/addressNumber", StringComparison.OrdinalIgnoreCase))
                hnr ??= value;
            else if (type.Contains("addressNumberExtension", StringComparison.OrdinalIgnoreCase))
                hnrZus ??= value;
        }
        return (hnr, hnrZus);
    }

    /// <summary>E.g. ".../AdministrativeHierarchyLevel/6thOrder" → "6thOrder".</summary>
    private static string? LevelOrdinal(XElement adminUnitName)
    {
        var levelHref = adminUnitName.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "level")
            ?.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value;
        if (levelHref is null) return null;

        var lastSlash = levelHref.LastIndexOf('/');
        return lastSlash >= 0 ? levelHref[(lastSlash + 1)..] : levelHref;
    }

    /// <summary>
    /// Maps an INSPIRE AdministrativeHierarchyLevel to the AGS length of the equivalent German
    /// admin level, for states that don't provide alternativeIdentifier (AGS codes).
    /// Returns 0 for levels that should be skipped.
    /// </summary>
    private static int SyntheticAgsLength(string? ordinal, bool isCityState) => isCityState
        ? ordinal switch
        {
            "2ndOrder" => 8,  // Hamburg/Berlin: the Land IS the Gemeinde
            "3rdOrder" => 9,  // Bezirk
            "4thOrder" => 10, // Stadtteil
            _          => 0,  // Country; 5thOrder+ are internal sub-districts with ugly display names
        }
        : ordinal switch
        {
            "2ndOrder" => 2,  // Bundesland
            "3rdOrder" => 3,  // Regierungsbezirk
            "4thOrder" => 5,  // Kreis
            "6thOrder" => 8,  // Gemeinde
            _          => 0,  // Country; 5thOrder is the Gemeindeverband (Amt/Verwaltungsverband)
        };

    private static string? FirstText(XElement component)
    {
        // SH has AdminUnitNames whose only text is whitespace; treat those as unnamed.
        var text = component.Descendants().FirstOrDefault(e => e.Name.LocalName == "text")?.Value;
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string GmlId(XElement element) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value ?? "";

    private static Polygon? ParsePolygon(XElement? gmlPolygon)
    {
        if (gmlPolygon is null) return null;

        var exterior = ParseRing(gmlPolygon.Elements().FirstOrDefault(e => e.Name.LocalName == "exterior"));
        if (exterior is null) return null;

        var interiors = gmlPolygon.Elements().Where(e => e.Name.LocalName == "interior")
            .Select(ParseRing)
            .OfType<LinearRing>()
            .ToArray();

        try
        {
            return GeometryFactory.CreatePolygon(exterior, interiors);
        }
        catch (ArgumentException)
        {
            // Malformed ring (e.g. unclosed) — skip this parcel rather than fail the whole page.
            return null;
        }
    }

    private static LinearRing? ParseRing(XElement? exteriorOrInterior)
    {
        if (exteriorOrInterior is null) return null;

        // Simple case (SH/BW/BB): LinearRing > posList — a single posList with all coordinates.
        // Complex case (HH): Ring > curveMember* > Curve > LineStringSegment > posList — many
        // posLists, one per edge. Collect and concatenate them, deduplicating shared endpoints.
        var posLists = exteriorOrInterior.Descendants()
            .Where(e => e.Name.LocalName == "posList" && !string.IsNullOrWhiteSpace(e.Value))
            .ToList();
        if (posLists.Count == 0) return null;

        var allCoords = new List<Coordinate>();
        foreach (var posList in posLists)
        {
            // 3D data lists x y z per vertex; only x and y matter for the join.
            var dimension = SrsDimension(posList);
            var raw = posList.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (raw.Length < 2 * dimension || raw.Length % dimension != 0) continue;

            for (var i = 0; i < raw.Length; i += dimension)
            {
                if (!double.TryParse(raw[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ||
                    !double.TryParse(raw[i + 1], NumberStyles.Any, CultureInfo.InvariantCulture, out var y))
                    return null;

                var coord = new Coordinate(x, y);
                // Skip duplicate point at segment boundary (each segment starts where the previous ended).
                if (allCoords.Count > 0 && allCoords[^1].Equals2D(coord))
                    continue;
                allCoords.Add(coord);
            }
        }

        if (allCoords.Count < 4) return null;

        try
        {
            return GeometryFactory.CreateLinearRing(allCoords.ToArray());
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>srsDimension of the nearest element declaring it (posList or an ancestor); 2 by default.</summary>
    private static int SrsDimension(XElement posList)
    {
        var declared = posList.AncestorsAndSelf()
            .Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "srsDimension")?.Value)
            .FirstOrDefault(v => v is not null);
        return int.TryParse(declared, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dimension) && dimension >= 2
            ? dimension
            : 2;
    }

    private static Point? ParsePoint(XElement? gmlPoint)
    {
        var pos = gmlPoint?.Elements().FirstOrDefault(e => e.Name.LocalName == "pos")?.Value;
        if (string.IsNullOrWhiteSpace(pos)) return null;

        var parts = pos.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        if (!double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var x)) return null;
        if (!double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var y)) return null;

        return GeometryFactory.CreatePoint(new Coordinate(x, y));
    }
}
