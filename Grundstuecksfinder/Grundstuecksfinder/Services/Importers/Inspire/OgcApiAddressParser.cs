using System.Text.Json;
using NetTopologySuite.Geometries;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// Parses the ALKIS-native "Hauskoordinaten" schema an OGC API Features "items" endpoint answers
/// with (GeoJSON), for sources whose INSPIRE ad:Address WFS is too slow to use (see
/// <see cref="InspireSourceOptions.UseOgcApiAddresses"/>). Not an INSPIRE schema, so its field
/// names are specific to this one dataset (Saarland's, so far): STN street, HNR/ADZ house number
/// and its suffix, PLZ, ONM the place name — there is no separate Ortsteil level, so ONM becomes
/// both Ort and Gemeinde.
/// </summary>
public static class OgcApiAddressParser
{
    private static readonly GeometryFactory GeometryFactory = new();

    public static WfsPage<AddressFeature> ParseAddresses(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseAddresses(doc);
    }

    public static WfsPage<AddressFeature> ParseAddresses(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (!root.TryGetProperty("features", out var features))
            throw new WfsResponseException("Expected a FeatureCollection with a \"features\" array.");

        var addresses = new List<AddressFeature>();
        var featureCount = 0;
        foreach (var feature in features.EnumerateArray())
        {
            featureCount++;
            if (!feature.TryGetProperty("properties", out var props)) continue;

            var point = ParsePoint(props);
            if (point is null) continue; // nothing to join without a location

            var str = PlaceNameNormalizer.RepairStreet(GetString(props, "STN"));
            var hnr = GetString(props, "HNR");
            var hnrZus = GetString(props, "ADZ");
            var plz = PostalCode.Normalize(GetString(props, "PLZ"));
            var ort = PlaceNameNormalizer.NormalizePlace(GetString(props, "ONM"));

            addresses.Add(new AddressFeature(point, str, hnr, hnrZus, plz, ort, ort));
        }

        return new WfsPage<AddressFeature>(addresses, featureCount, new HashSet<string>());
    }

    /// <summary>
    /// XCOORD/YCOORD carry EPSG:25832 metres with the UTM zone folded into the easting's
    /// ten-millions digit ("32366655.727" is zone 32 + easting 366655.727 — Saarland lies
    /// entirely in zone 32), which avoids reprojecting the feature's WGS84 geometry.
    /// </summary>
    private static Point? ParsePoint(JsonElement props)
    {
        if (!TryGetDouble(props, "XCOORD", out var xcoord) || !TryGetDouble(props, "YCOORD", out var ycoord))
            return null;
        return GeometryFactory.CreatePoint(new Coordinate(xcoord - 32_000_000, ycoord));
    }

    private static bool TryGetDouble(JsonElement props, string name, out double value)
    {
        value = 0;
        return props.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value);
    }

    /// <summary>Handles both JSON strings and numbers (HNR/PLZ come back as numbers).</summary>
    private static string? GetString(JsonElement props, string name)
    {
        if (!props.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            _ => null,
        };
    }
}
