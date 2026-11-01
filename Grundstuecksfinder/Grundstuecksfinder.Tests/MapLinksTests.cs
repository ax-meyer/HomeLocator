using System.Globalization;
using FluentAssertions;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services;
using Xunit;

namespace Grundstuecksfinder.Tests;

public sealed class MapLinksTests
{
    private static readonly Property Berlin = new()
    {
        Str = "Bergstraße", Hnr = "19", HnrZus = "A", Plz = "10115", Ort = "Mitte", Source = "be",
    };

    private static readonly (double Lat, double Lon) Pin = (52.531174, 13.395007);

    [Fact]
    public void Google_SearchesTheAddress_EvenWithCoordinates() =>
        MapLinks.Google(Berlin).Should().Be("https://www.google.com/maps/search/Bergstra%C3%9Fe%2019A%2C%2010115%2C%20Mitte");

    [Theory]
    [InlineData("17", "uneindeutig", "Kinderhauser%20Stra%C3%9Fe%2017%2C%2048149%2C%20M%C3%BCnster")]
    [InlineData("uneindeutig", null, "Kinderhauser%20Stra%C3%9Fe%2C%2048149%2C%20M%C3%BCnster")]
    [InlineData("uneindeutig", "uneindeutig", "Kinderhauser%20Stra%C3%9Fe%2C%2048149%2C%20M%C3%BCnster")]
    public void Google_LeavesNrwsUneindeutigPlaceholderOut(string hnr, string? suffix, string query)
    {
        var property = new Property { Str = "Kinderhauser Straße", Hnr = hnr, HnrZus = suffix, Plz = "48149", Ort = "Münster", Source = "nrw" };

        MapLinks.Google(property).Should().Be($"https://www.google.com/maps/search/{query}");
    }

    [Fact]
    public void Coordinates_AreTheRowsOwn_RoundedToSixDecimals() =>
        MapLinks.Coordinates(new Property { Lat = 52.530113f, Lon = 13.395007f }).Should().Be((52.530113, 13.395007));

    [Fact]
    public void Coordinates_WithoutAPosition_AreDefault() =>
        MapLinks.Coordinates(new Property { Lat = 52.53f }).Should().Be(default((double, double)));

    [Fact]
    public void Osm_WithCoordinates_SetsAMarker() =>
        MapLinks.Osm(Berlin, Pin).Should().Be("https://www.openstreetmap.org/?mlat=52.531174&mlon=13.395007&zoom=17");

    [Fact]
    public void Osm_WithoutCoordinates_SearchesTheAddress() =>
        MapLinks.Osm(Berlin, default).Should().Be("https://www.openstreetmap.org/search?query=Bergstra%C3%9Fe%2019A%2C%2010115%2C%20Mitte");

    [Fact]
    public void Flood_Nrw_CentresBothMapWidgetsOnTheCoordinates() =>
        MapLinks.Flood("nrw", (50.7193, 7.1398)).Should().Be(
            "https://www.giscloud.nrw.de/arcgis/apps/experiencebuilder/experience/?id=222e772d0d4543819ddaae90346e2c51"
            + "#widget_567=center:7.1398%2C50.7193%2C4326,scale:5000&widget_568=center:7.1398%2C50.7193%2C4326,scale:5000");

    [Fact]
    public void Flood_NrwWithoutCoordinates_OpensTheMap() =>
        MapLinks.Flood("nrw", default).Should().Be("https://www.hochwasserkarten.nrw.de/");

    [Theory]
    [InlineData("sh")]
    [InlineData("be")]
    public void Flood_OtherStates_HaveNone(string source) =>
        MapLinks.Flood(source, Pin).Should().BeNull();

    [Fact]
    public void Noise_Nrw_IsThePlainMap_ItTakesNoPosition() =>
        MapLinks.Noise("nrw").Should().Be("https://www.umgebungslaerm-kartierung.nrw.de/");

    [Fact]
    public void Noise_StatesWithoutAMap_HaveNone() =>
        MapLinks.Noise("be").Should().BeNull();

    [Fact]
    public void Coordinates_UseADot_WhateverTheCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            MapLinks.Osm(Berlin, Pin).Should().Contain("mlat=52.531174&mlon=13.395007");
            MapLinks.Flood("nrw", Pin).Should().Contain("center:13.395007%2C52.531174%2C4326");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
