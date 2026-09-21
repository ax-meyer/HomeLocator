using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public class WfsGmlParserTests
{
    private static FileStream OpenFixture(string fileName) =>
        File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Importers", "Inspire", "TestData", fileName));

    [Fact]
    public void ParseCadastralParcels_SkipsFeatureMissingAreaValue()
    {
        using var stream = OpenFixture("cadastral_parcels.gml");

        var parcels = WfsGmlParser.ParseCadastralParcels(stream).ToList();

        parcels.Should().HaveCount(2, "the third parcel has no areaValue and should be skipped, not thrown on");
    }

    [Fact]
    public void ParseCadastralParcels_ParsesAreaAndPolygonFromPosList()
    {
        using var stream = OpenFixture("cadastral_parcels.gml");

        var parcels = WfsGmlParser.ParseCadastralParcels(stream).ToList();

        parcels[0].AreaM2.Should().Be(1250.5);
        parcels[0].Polygon.Contains(parcels[0].Polygon.Factory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(560050, 5990050)))
            .Should().BeTrue("the point lies inside the parcel's exterior ring");
    }

    [Fact]
    public void ParseAddresses_SkipsFeatureWithoutAGeometry()
    {
        using var stream = OpenFixture("addresses.gml");

        var addresses = WfsGmlParser.ParseAddresses(stream).ToList();

        addresses.Should().HaveCount(3, "Address_4_nopoint has no position and cannot be joined, so it must be skipped");
    }

    [Fact]
    public void ParseAddresses_ResolvesThoroughfareAndPostalDescriptorComponents()
    {
        using var stream = OpenFixture("addresses.gml");

        var address = WfsGmlParser.ParseAddresses(stream).First();

        address.Str.Should().Be("Am Kirchhof");
        address.Plz.Should().Be("24649");
        address.Hnr.Should().Be("12");
        address.HnrZus.Should().BeNull();
    }

    [Fact]
    public void ParseAddresses_PrefersMostSpecificAdminUnitForOrtAndCapsGemeindeAtEightDigitAgs()
    {
        using var stream = OpenFixture("addresses.gml");

        var addresses = WfsGmlParser.ParseAddresses(stream).ToList();
        var withoutOrtsteil = addresses[0];
        var withOrtsteil = addresses[1];

        withoutOrtsteil.Gemeinde.Should().Be("Beispielgemeinde");
        withoutOrtsteil.Ort.Should().Be("Beispielgemeinde", "with no Ortsteil, the Gemeinde is also the most specific unit");

        withOrtsteil.Hnr.Should().Be("4");
        withOrtsteil.HnrZus.Should().Be("a");
        withOrtsteil.Gemeinde.Should().Be("Beispielgemeinde", "Gemeinde stays capped at the 8-digit AGS unit");
        withOrtsteil.Ort.Should().Be("Beispielortsteil", "Ort prefers the longer, more specific Ortsteil AGS");
    }

    [Fact]
    public void ParseAddresses_DanglingComponentReference_ResolvesToNullFieldsInsteadOfThrowing()
    {
        using var stream = OpenFixture("addresses.gml");

        var dangling = WfsGmlParser.ParseAddresses(stream).Single(a => a.Str is null && a.Plz is null && a.Gemeinde is null);

        dangling.Location.X.Should().Be(999000);
        dangling.Location.Y.Should().Be(5999000);
    }

    [Fact]
    public void ParseAddresses_HamburgStyle_FallsBackToLevelHierarchyWhenNoAgsCode()
    {
        using var stream = OpenFixture("addresses_hamburg.gml");

        var address = WfsGmlParser.ParseAddresses(stream).Single();

        address.Str.Should().Be("Aalheitengraben");
        address.Hnr.Should().Be("4");
        address.Plz.Should().Be("22359");
        address.Gemeinde.Should().Be("Hamburg", "2ndOrder in a city-state is the municipality");
        address.Ort.Should().Be("Volksdorf", "4thOrder (Stadtteil) is the most specific usable level");
    }

    [Fact]
    public void ParseAddresses_LevelHierarchy_UsesSixthOrderAsGemeindeNotLandOrKreis()
    {
        using var stream = OpenFixture("addresses_levels.gml");

        var bw = WfsGmlParser.ParseAddresses(stream).First();

        bw.Gemeinde.Should().Be("Bruchsal", "6thOrder is the Gemeinde; 2ndOrder is the (misspelled) Land");
        bw.Ort.Should().Be("Bruchsal");
        bw.Plz.Should().Be("76646");
    }

    [Fact]
    public void ParseAddresses_HessenStyle_ResolvesGemeindeAndRepairsCorruptedStrasse()
    {
        using var stream = OpenFixture("addresses_levels.gml");

        var he = WfsGmlParser.ParseAddresses(stream).Last();

        he.Gemeinde.Should().Be("Frankfurt am Main", "not \"Hessen\" (2ndOrder)");
        he.Ort.Should().Be("Frankfurt am Main", "not the Kreis \"Kreisfreie Stadt Frankfurt am Main\" (4thOrder)");
        he.Str.Should().Be("Adam-Riese-Straße");
        he.Plz.Should().BeNull("HE publishes no PostalDescriptor");
    }

    [Fact]
    public void ParseAddresses_OfficialNames_AreNormalizedToPlainPlaceNames()
    {
        using var stream = OpenFixture("addresses_official_names.gml");

        var addresses = WfsGmlParser.ParseAddresses(stream).ToList();

        addresses.Select(a => (a.Gemeinde, a.Ort)).Should().Equal(
            ("Pirna", "Pirna"),     // SN: "Stadt Pirna"
            ("Kiel", "Kiel"),       // SH: "Kiel, Landeshauptstadt" (plus a whitespace-only 3rdOrder name)
            ("Cottbus", "Cottbus")); // BB: "Cottbus [Chóśebuz]"
    }
}
