using System.Text;
using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Postcodes;
using NetTopologySuite.Geometries;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Postcodes;

/// <summary>
/// Postcode areas in lon/lat looked up with points in a source's UTM CRS. The reference points
/// in Potsdam (EPSG:25833) were projected independently: (367000, 5807000) is 13.0452°E
/// 52.3971°N, (368000, 5807000) is 13.0599°E 52.3973°N.
/// </summary>
public sealed class PostcodeAreaIndexTests
{
    private static readonly Point West = new(367000, 5807000);
    private static readonly Point East = new(368000, 5807000);
    private static readonly Envelope PotsdamBbox = new(360000, 375000, 5800000, 5815000);

    // Like the real file: metadata before "features", Polygons and MultiPolygons, a few
    // non-German postcodes along the border, and areas far outside any one source.
    private const string GeoJson = """
        {
          "copyright": "© OpenStreetMap contributors",
          "license": "ODbL-1.0",
          "type": "FeatureCollection",
          "features": [
            { "type": "Feature", "properties": { "postcode": "14469" },
              "geometry": { "type": "Polygon", "coordinates": [[[13.0, 52.35], [13.1, 52.35], [13.1, 52.45], [13.0, 52.45], [13.0, 52.35]]] } },
            { "type": "Feature", "properties": { "postcode": "14467" },
              "geometry": { "type": "Polygon", "coordinates": [[[13.03, 52.39], [13.052, 52.39], [13.052, 52.405], [13.03, 52.405], [13.03, 52.39]]] } },
            { "type": "Feature", "properties": { "postcode": "14471" },
              "geometry": { "type": "MultiPolygon", "coordinates": [
                [[[13.2, 52.5], [13.21, 52.5], [13.21, 52.51], [13.2, 52.51], [13.2, 52.5]]],
                [[[13.055, 52.39], [13.065, 52.39], [13.065, 52.40], [13.055, 52.40], [13.055, 52.39]]]] } },
            { "type": "Feature", "properties": { "postcode": "66-400" },
              "geometry": { "type": "Polygon", "coordinates": [[[13.058, 52.396], [13.062, 52.396], [13.062, 52.398], [13.058, 52.398], [13.058, 52.396]]] } },
            { "type": "Feature", "properties": { "postcode": "80331" },
              "geometry": { "type": "Polygon", "coordinates": [[[11.56, 48.13], [11.58, 48.13], [11.58, 48.15], [11.56, 48.15], [11.56, 48.13]]] } }
          ]
        }
        """;

    private static PostcodeAreaIndex Load(Envelope bbox) =>
        PostcodeAreaIndex.Load(new MemoryStream(Encoding.UTF8.GetBytes(GeoJson)), 25833, bbox);

    [Fact]
    public void Load_KeepsOnlyGermanAreasTouchingTheBoundingBox()
    {
        Load(PotsdamBbox).Count.Should().Be(3, "Munich is far away and 66-400 isn't a German postcode");
    }

    [Fact]
    public void FindPostcode_OverlappingAreas_TheSmallestWins()
    {
        Load(PotsdamBbox).FindPostcode(West).Should().Be("14467", "14467 lies inside the larger 14469");
    }

    [Fact]
    public void FindPostcode_MultiPolygon_MatchesAnyPart()
    {
        Load(PotsdamBbox).FindPostcode(East).Should().Be("14471", "the foreign 66-400 inside it is skipped");
    }

    [Fact]
    public void FindPostcode_OutsideEveryArea_ReturnsNull()
    {
        Load(PotsdamBbox).FindPostcode(new Point(400000, 5807000)).Should().BeNull();
    }

    [Fact]
    public void Load_BoundingBoxElsewhere_KeepsNothing()
    {
        var index = Load(new Envelope(600000, 610000, 5300000, 5310000));

        index.Count.Should().Be(0);
        index.FindPostcode(West).Should().BeNull();
    }
}
