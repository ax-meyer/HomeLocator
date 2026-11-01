using Grundstuecksfinder.Models;

namespace Grundstuecksfinder.Services;

/// <summary>
/// The outgoing map links of a result card. Coordinates are WGS84; default((double,double))
/// means the row has none, in which case each link falls back to what works without them.
/// </summary>
/// <remarks>Coordinates go through FormattableString.Invariant: a German server culture would write commas.</remarks>
public static class MapLinks
{
    // The ArcGIS Experience Builder app that hochwasserkarten.nrw.de shows in an iframe. Only the
    // app itself reads a position (from its URL hash, per map widget), the wrapper passes nothing on.
    private const string FloodApp =
        "https://www.giscloud.nrw.de/arcgis/apps/experiencebuilder/experience/?id=222e772d0d4543819ddaae90346e2c51";

    /// <summary>
    /// The row's own position (the official one from the source), rounded to six decimals —
    /// about 10 cm — so a link shows 52.530113 rather than the float's 52.530113220214844;
    /// default where the source has none.
    /// </summary>
    public static (double Lat, double Lon) Coordinates(Property property) =>
        property is { Lat: { } lat, Lon: { } lon } ? (Math.Round((double)lat, 6), Math.Round((double)lon, 6)) : default;

    /// <summary>
    /// Always a search for the address: Google finds even houses OSM doesn't know (within ~10 m
    /// in Berlin's courtyards) and then shows the address and its place card, where a pin at bare
    /// coordinates would only show "52°31'48.2"N 13°23'52.9"E".
    /// </summary>
    public static string Google(Property property) =>
        $"https://www.google.com/maps/search/{AddressQuery(property)}";

    /// <summary>
    /// A marker at the coordinates where known; otherwise OSM's own address search, which only
    /// lists the street when OSM lacks the house number.
    /// </summary>
    public static string Osm(Property property, (double Lat, double Lon) coords) => coords != default
        ? FormattableString.Invariant($"https://www.openstreetmap.org/?mlat={coords.Lat}&mlon={coords.Lon}&zoom=17")
        : $"https://www.openstreetmap.org/search?query={AddressQuery(property)}";

    /// <summary>
    /// The state's noise map, or null where there is none to link. Neither NRW's nor SH's map
    /// reads a position from its URL (NRW's code never looks at it), so both open as they are.
    /// </summary>
    public static string? Noise(string source) => source switch
    {
        "nrw" => "https://www.umgebungslaerm-kartierung.nrw.de/",
        "sh" => "https://danord.gdi-sh.de/viewer/resources/apps/umgebungslaerm/index.html?lang=de",
        _ => null,
    };

    /// <summary>
    /// The state's flood map, centred on the coordinates where known, or null where there is none
    /// to link. SH's map is left out: its deep links redirect to a Cadenza login.
    /// </summary>
    public static string? Flood(string source, (double Lat, double Lon) coords) => source switch
    {
        // widget_567 is the desktop map, widget_568 the tablet one; each layout reads only its own.
        "nrw" when coords != default => FormattableString.Invariant(
            $"{FloodApp}#widget_567=center:{coords.Lon}%2C{coords.Lat}%2C4326,scale:5000&widget_568=center:{coords.Lon}%2C{coords.Lat}%2C4326,scale:5000"),
        "nrw" => "https://www.hochwasserkarten.nrw.de/",
        _ => null,
    };

    private static string AddressQuery(Property property) =>
        Uri.EscapeDataString(
            string.Join(", ",
                new[] { $"{property.Str} {Known(property.Hnr)}{Known(property.HnrZus)}".Trim(), property.Plz, property.Ort }
                    .Where(s => !string.IsNullOrWhiteSpace(s))));

    // NRW's Grundsteuer CSV writes "uneindeutig" where the cadastre's number or suffix isn't
    // unique; searched for, "Kinderhauser Straße 17uneindeutig" finds nothing, "… 17" the house.
    private static string? Known(string? part) =>
        string.Equals(part?.Trim(), "uneindeutig", StringComparison.OrdinalIgnoreCase) ? null : part;
}
