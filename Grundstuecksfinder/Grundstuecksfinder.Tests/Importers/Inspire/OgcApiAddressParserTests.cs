using System.Text;
using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public class OgcApiAddressParserTests
{
    private static MemoryStream Json(string body) => new(Encoding.UTF8.GetBytes(body));

    private const string SampleCollection = """
        {
          "type": "FeatureCollection",
          "numberMatched": 3,
          "numberReturned": 3,
          "features": [
            {
              "type": "Feature",
              "properties": {
                "gml_id": "Hauskoordinaten.1", "LAN": 10, "RBZ": 0, "KRS": 45, "GMD": 117,
                "HNR": 16, "ADZ": "a", "XCOORD": 32366655.727, "YCOORD": 5458258.959,
                "STN": "Schulstraße", "PLZ": 66386, "ONM": "St. Ingbert"
              },
              "geometry": { "type": "Point", "coordinates": [7.167, 49.262] }
            },
            {
              "type": "Feature",
              "properties": {
                "gml_id": "Hauskoordinaten.2", "LAN": 10, "RBZ": 0, "KRS": 41, "GMD": 100,
                "HNR": 3, "ADZ": null, "XCOORD": 32365000.0, "YCOORD": 5457000.0,
                "STN": "Rathausplatz", "PLZ": null, "ONM": "Saarbrücken"
              },
              "geometry": { "type": "Point", "coordinates": [7.15, 49.25] }
            },
            {
              "type": "Feature",
              "properties": {
                "gml_id": "Hauskoordinaten.3", "LAN": 10, "RBZ": 0, "KRS": 45, "GMD": 117,
                "HNR": 1, "STN": "Ohne Koordinate", "PLZ": 66386, "ONM": "St. Ingbert"
              },
              "geometry": null
            }
          ]
        }
        """;

    [Fact]
    public void ParseAddresses_SkipsFeatureWithoutCoordinates()
    {
        using var stream = Json(SampleCollection);

        var page = OgcApiAddressParser.ParseAddresses(stream);

        page.MemberCount.Should().Be(3, "every returned feature counts, including the skipped one");
        page.Features.Should().HaveCount(2, "the third feature has no XCOORD/YCOORD and cannot be joined");
    }

    [Fact]
    public void ParseAddresses_StripsTheZonePrefixFromXCoord()
    {
        using var stream = Json(SampleCollection);

        var address = OgcApiAddressParser.ParseAddresses(stream).Features[0];

        address.Location.X.Should().BeApproximately(366655.727, 0.001, "the leading \"32\" (UTM zone) must be removed from XCOORD, not just its first digits");
        address.Location.Y.Should().Be(5458258.959, "YCOORD is already the northing");
    }

    [Fact]
    public void ParseAddresses_MapsFieldsAndOnmToBothOrtAndGemeinde()
    {
        using var stream = Json(SampleCollection);

        var address = OgcApiAddressParser.ParseAddresses(stream).Features[0];

        address.Str.Should().Be("Schulstraße");
        address.Hnr.Should().Be("16");
        address.HnrZus.Should().Be("a");
        address.Plz.Should().Be("66386");
        address.Gemeinde.Should().Be("St. Ingbert", "this source has no separate Ortsteil level");
        address.Ort.Should().Be("St. Ingbert");
    }

    [Fact]
    public void ParseAddresses_MissingPlzBecomesNull()
    {
        using var stream = Json(SampleCollection);

        var address = OgcApiAddressParser.ParseAddresses(stream).Features[1];

        address.Plz.Should().BeNull();
        address.HnrZus.Should().BeNull();
    }

    [Fact]
    public void ParseAddresses_MissingFeaturesArray_Throws()
    {
        using var stream = Json("""{"type": "FeatureCollection"}""");

        var act = () => OgcApiAddressParser.ParseAddresses(stream);

        act.Should().Throw<WfsResponseException>();
    }
}
