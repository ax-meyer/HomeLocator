using System.Xml.Linq;
using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using NetTopologySuite.Geometries;
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

        var parcels = WfsGmlParser.ParseCadastralParcels(stream).Features.ToList();

        parcels.Should().HaveCount(2, "the third parcel has no areaValue and should be skipped, not thrown on");
    }

    [Fact]
    public void ParseCadastralParcels_ParsesAreaAndPolygonFromPosList()
    {
        using var stream = OpenFixture("cadastral_parcels.gml");

        var parcels = WfsGmlParser.ParseCadastralParcels(stream).Features.ToList();

        parcels[0].AreaM2.Should().Be(1250.5);
        parcels[0].Geometry.Contains(parcels[0].Geometry.Factory.CreatePoint(new Coordinate(560050, 5990050)))
            .Should().BeTrue("the point lies inside the parcel's exterior ring");
    }

    private static readonly ParcelFeatureTypeOptions AlkisVereinfacht = new()
    {
        TypeName = "ave:Flurstueck",
        AreaField = "flaeche",
        GemeindeField = "gemeinde",
        LagebezeichnungField = "lagebeztxt",
    };

    [Fact]
    public void ParseParcels_AlkisVereinfacht_ReadsAreaGemeindeAndLagebezeichnung()
    {
        using var stream = OpenFixture("parcels_alkis_vereinfacht.gml");

        var page = WfsGmlParser.ParseParcels(stream, AlkisVereinfacht);

        page.MemberCount.Should().Be(3);
        page.Features.Should().HaveCount(2, "the third parcel has no flaeche and is skipped");
        page.Features[0].AreaM2.Should().Be(405);
        page.Features[0].Gemeinde.Should().Be("Mainz");
        page.Features[0].Lagebezeichnung.Should().Be("Löwenhofstraße 5; Vordere Synagogenstraße 2");
        page.Features[0].Geometry.Contains(page.Features[0].Geometry.Factory.CreatePoint(new Coordinate(447010, 5539010)))
            .Should().BeTrue("the MultiSurface's polygon is the parcel");
        page.Features[1].AreaM2.Should().Be(1210.5);
        page.Features[1].Lagebezeichnung.Should().BeNull("a blank field reads as none");
        page.SrsNames.Should().Equal("urn:ogc:def:crs:EPSG::25832");
    }

    [Fact]
    public void ParseParcels_FieldsNotConfigured_AreNotRead()
    {
        using var stream = OpenFixture("parcels_alkis_vereinfacht.gml");

        var parcels = WfsGmlParser.ParseParcels(stream, new ParcelFeatureTypeOptions { TypeName = "ave:Flurstueck", AreaField = "flaeche" }).Features;

        parcels.Should().HaveCount(2).And.OnlyContain(p => p.Gemeinde == null && p.Lagebezeichnung == null);
    }

    [Fact]
    public void ParseParcels_OtherFeatureType_FindsNothing()
    {
        using var stream = OpenFixture("parcels_alkis_vereinfacht.gml");

        var page = WfsGmlParser.ParseCadastralParcels(stream);

        page.Features.Should().BeEmpty();
        page.MemberCount.Should().Be(3, "every member still counts towards a full page");
    }

    private static readonly Grundstuecksfinder.Services.Importers.Inspire.Addresses.AddressFieldOptions BremenFields = new()
    {
        Street = "stn", HouseNumber = "hnr", HouseNumberSuffix = "adz", Plz = "plz", Ort = "onm", Gemeinde = "onm",
    };

    [Fact]
    public void ParseFlatAddresses_ReadsTheMappedFields()
    {
        using var stream = OpenFixture("addresses_flat_bremen.gml");

        var page = WfsGmlParser.ParseFlatAddresses(XDocument.Load(stream), "adressen", BremenFields);

        page.MemberCount.Should().Be(3);
        page.Features.Should().HaveCount(2, "the third address has no point to join by");
        var first = page.Features[0];
        (first.Str, first.Hnr, first.HnrZus, first.Plz, first.Ort, first.Gemeinde)
            .Should().Be(("Mittelstraße", "9", "a", "27568", "Bremerhaven", "Bremerhaven"));
        first.Location.X.Should().Be(472230.052);
        first.Location.Y.Should().Be(5932721.821);
        page.Features[1].HnrZus.Should().BeNull("the element is absent");
        page.SrsNames.Should().Equal("urn:ogc:def:crs:EPSG::25832");
    }

    [Fact]
    public void ParseFlatAddresses_WithoutAnOrtField_UsesTheGemeinde()
    {
        using var stream = OpenFixture("addresses_flat_bremen.gml");

        var fields = new Grundstuecksfinder.Services.Importers.Inspire.Addresses.AddressFieldOptions
        {
            Street = "stn", HouseNumber = "hnr", Gemeinde = "onm",
        };
        var address = WfsGmlParser.ParseFlatAddresses(XDocument.Load(stream), "adressen", fields).Features[0];

        (address.Ort, address.Gemeinde, address.Plz, address.HnrZus).Should().Be(("Bremerhaven", "Bremerhaven", null, null));
    }

    [Fact]
    public void ParseAddresses_SkipsFeatureWithoutAGeometry()
    {
        using var stream = OpenFixture("addresses.gml");

        var addresses = WfsGmlParser.ParseAddresses(stream).Features.ToList();

        addresses.Should().HaveCount(3, "Address_4_nopoint has no position and cannot be joined, so it must be skipped");
    }

    [Fact]
    public void ParseAddresses_ResolvesThoroughfareAndPostalDescriptorComponents()
    {
        using var stream = OpenFixture("addresses.gml");

        var address = WfsGmlParser.ParseAddresses(stream).Features[0];

        address.Str.Should().Be("Am Kirchhof");
        address.Plz.Should().Be("24649");
        address.Hnr.Should().Be("12");
        address.HnrZus.Should().BeNull();
    }

    [Fact]
    public void ParseAddresses_PrefersMostSpecificAdminUnitForOrtAndCapsGemeindeAtEightDigitAgs()
    {
        using var stream = OpenFixture("addresses.gml");

        var addresses = WfsGmlParser.ParseAddresses(stream).Features.ToList();
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

        var dangling = WfsGmlParser.ParseAddresses(stream).Features.Single(a => a.Str is null && a.Plz is null && a.Gemeinde is null);

        dangling.Location.X.Should().Be(999000);
        dangling.Location.Y.Should().Be(5999000);
    }

    [Fact]
    public void ParseAddresses_HamburgStyle_FallsBackToLevelHierarchyWhenNoAgsCode()
    {
        using var stream = OpenFixture("addresses_hamburg.gml");

        var address = WfsGmlParser.ParseAddresses(stream, isCityState: true).Features.Single();

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

        var bw = WfsGmlParser.ParseAddresses(stream).Features[0];

        bw.Gemeinde.Should().Be("Bruchsal", "6thOrder is the Gemeinde; 2ndOrder is the (misspelled) Land");
        bw.Ort.Should().Be("Bruchsal");
        bw.Plz.Should().Be("76646");
    }

    [Fact]
    public void ParseAddresses_WithoutOrtsteil_UsesPostNameAsOrtByDefault()
    {
        using var stream = OpenFixture("addresses_shared_postname.gml");

        var address = WfsGmlParser.ParseAddresses(stream).Features.Single();

        address.Ort.Should().Be("Petersdorf a. F.");
        address.Gemeinde.Should().Be("Fehmarn");
    }

    [Fact]
    public void ParseAddresses_PostNameDisabled_FallsBackToGemeinde()
    {
        using var stream = OpenFixture("addresses_shared_postname.gml");

        var address = WfsGmlParser.ParseAddresses(stream, usePostName: false).Features.Single();

        address.Ort.Should().Be("Fehmarn", "SH's postName is one arbitrary village per postcode");
        address.Gemeinde.Should().Be("Fehmarn");
        address.Plz.Should().Be("23769", "the postcode is still taken from the PostalDescriptor");
        address.Str.Should().Be("Breite Straße");
    }

    [Fact]
    public void ParseAddresses_HessenStyle_ResolvesGemeindeFromTheLevels()
    {
        using var stream = OpenFixture("addresses_levels.gml");

        var he = WfsGmlParser.ParseAddresses(stream).Features[^1];

        he.Gemeinde.Should().Be("Frankfurt am Main", "not \"Hessen\" (2ndOrder)");
        he.Ort.Should().Be("Frankfurt am Main", "not the Kreis \"Kreisfreie Stadt Frankfurt am Main\" (4thOrder)");
        he.Str.Should().Be("Adam-Riese-Straße");
        he.Plz.Should().BeNull("HE publishes no PostalDescriptor");
    }

    [Fact]
    public void ParseAddresses_OfficialNames_AreNormalizedToPlainPlaceNames()
    {
        using var stream = OpenFixture("addresses_official_names.gml");

        var addresses = WfsGmlParser.ParseAddresses(stream).Features.ToList();

        addresses.Select(a => (a.Gemeinde, a.Ort)).Should().Equal(
            ("Pirna", "Pirna"),     // SN: "Stadt Pirna"
            ("Kiel", "Kiel"),       // SH: "Kiel, Landeshauptstadt" (plus a whitespace-only 3rdOrder name)
            ("Cottbus", "Cottbus")); // BB: "Cottbus [Chóśebuz]"
    }

    private static WfsPage<ParcelFeature> ParseParcels(string members) => WfsGmlParser.ParseCadastralParcels(XDocument.Parse($"""
        <wfs:FeatureCollection xmlns:wfs="http://www.opengis.net/wfs/2.0" xmlns:gml="http://www.opengis.net/gml/3.2"
                               xmlns="http://inspire.ec.europa.eu/schemas/cp/4.0">{members}</wfs:FeatureCollection>
        """));

    [Fact]
    public void ParseCadastralParcels_MemberCountIncludesSkippedFeatures()
    {
        using var stream = OpenFixture("cadastral_parcels.gml");

        var page = WfsGmlParser.ParseCadastralParcels(stream);

        page.Features.Should().HaveCount(2);
        page.MemberCount.Should().Be(3, "a full page must be recognised even if a feature on it is malformed");
        page.SrsNames.Should().Equal("http://www.opengis.net/def/crs/epsg/0/25832");
    }

    [Fact]
    public void ParseCadastralParcels_MultiSurface_KeepsEveryPart()
    {
        var page = ParseParcels("""
            <wfs:member><CadastralParcel gml:id="CP_multi"><areaValue>300</areaValue><geometry>
              <gml:MultiSurface srsName="urn:ogc:def:crs:EPSG::25832">
                <gml:surfaceMember><gml:Polygon><gml:exterior><gml:LinearRing>
                  <gml:posList>0 0 10 0 10 10 0 10 0 0</gml:posList></gml:LinearRing></gml:exterior></gml:Polygon></gml:surfaceMember>
                <gml:surfaceMember><gml:Polygon><gml:exterior><gml:LinearRing>
                  <gml:posList>50 0 60 0 60 10 50 10 50 0</gml:posList></gml:LinearRing></gml:exterior></gml:Polygon></gml:surfaceMember>
              </gml:MultiSurface></geometry></CadastralParcel></wfs:member>
            """);

        var parcel = page.Features.Should().ContainSingle().Subject;
        parcel.Geometry.NumGeometries.Should().Be(2);
        parcel.Geometry.Contains(parcel.Geometry.Factory.CreatePoint(new Coordinate(55, 5))).Should().BeTrue();
    }

    [Fact]
    public void ParseCadastralParcels_ThreeDimensionalPosList_UsesXAndYOnly()
    {
        var page = ParseParcels("""
            <wfs:member><CadastralParcel gml:id="CP_3d"><areaValue>100</areaValue><geometry>
              <gml:Polygon srsName="urn:ogc:def:crs:EPSG::25832" srsDimension="3"><gml:exterior><gml:LinearRing>
                <gml:posList>0 0 5 10 0 5 10 10 5 0 10 5 0 0 5</gml:posList>
              </gml:LinearRing></gml:exterior></gml:Polygon></geometry></CadastralParcel></wfs:member>
            """);

        var parcel = page.Features.Should().ContainSingle().Subject;
        parcel.Geometry.Area.Should().Be(100, "x y z triples must not be read as x y pairs");
    }
}
