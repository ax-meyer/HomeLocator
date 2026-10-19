using FluentAssertions;
using Grundstuecksfinder.Services.Importers;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers;

public sealed class UtmToLonLatTests
{
    // Berlin, Ackerstraße 3E, as Berlin's address WFS serves it in each CRS.
    private const double Lon = 13.39878701;
    private const double Lat = 52.53011335;

    [Theory]
    [InlineData(25833, 391382.68999996, 5821206.30999967)]
    // Its own zone is 33; zone 32 is what most states use, extended this far east.
    [InlineData(25832, 798322.93739787, 5829098.236259)]
    public void Transform_MatchesThePublishersOwnReprojection(int epsg, double x, double y)
    {
        var (lon, lat) = new UtmToLonLat(epsg).Transform(x, y);

        // 1e-6° is ~10 cm, far below a pin's precision.
        lon.Should().BeApproximately(Lon, 1e-6);
        lat.Should().BeApproximately(Lat, 1e-6);
    }

    [Theory]
    [InlineData(25830)]
    [InlineData(25834)]
    [InlineData(4326)]
    public void Constructor_RejectsNonGermanUtmZones(int epsg)
    {
        var act = () => new UtmToLonLat(epsg);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
