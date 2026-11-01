using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Grundstuecksfinder.Services.Importers.Inspire.Addresses;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

/// <summary>
/// The shipped appsettings.json, bound the way Program.cs binds it: a misspelt property binds
/// nothing and silently falls back to its default, which only assertions like these notice.
/// </summary>
public sealed class InspireSourcesConfigTests
{
    private static List<InspireSourceOptions> ShippedSources() =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build()
            .GetSection("Import:Inspire:Sources").Get<List<InspireSourceOptions>>()!;

    [Fact]
    public void ShippedSources_AreValid() =>
        InspireSourceOptions.Validate(ShippedSources(), ["nrw"]).Should().BeEmpty();

    [Theory]
    [InlineData("sh", AddressSourceType.InspireWfs)]
    [InlineData("sn", AddressSourceType.InspireWfs)]
    [InlineData("bb", AddressSourceType.InspireWfs)]
    [InlineData("bw", AddressSourceType.HkFile)]
    [InlineData("ni", AddressSourceType.InspireWfs)]
    [InlineData("hh", AddressSourceType.InspireWfsStartIndex)]
    [InlineData("he", AddressSourceType.HkFile)]
    [InlineData("sl", AddressSourceType.FlatWfs)]
    [InlineData("rp", AddressSourceType.ParcelLagebezeichnung)]
    [InlineData("th", AddressSourceType.ParcelLagebezeichnung)]
    [InlineData("hb", AddressSourceType.FlatWfs)]
    [InlineData("be", AddressSourceType.FlatWfs)]
    public void ShippedSources_UseTheirAddressSource(string source, AddressSourceType type) =>
        ShippedSources().Single(s => s.Source == source).AddressSource.Type.Should().Be(type);

    [Fact]
    public void ShippedSources_BadenWuerttembergReadsItsHauskoordinatenFile()
    {
        var bw = ShippedSources().Single(s => s.Source == "bw");

        bw.AddressSource.Locator.Should().Be(HkFileLocatorType.StaticUrl);
        bw.AddressSource.Member.Should().Be("adressen-bw.txt");
        bw.AddressSource.Qualities.Should().Equal("A", "B");
        bw.FillMissingPlzFromPostcodeAreas.Should().BeTrue("the file's postal columns are empty");
    }

    [Fact]
    public void ShippedSources_HessenFindsItsHauskoordinatenFileInTheDownloadCenter()
    {
        var he = ShippedSources().Single(s => s.Source == "he");

        he.AddressSource.Locator.Should().Be(HkFileLocatorType.HessenDownloadCenter);
        he.AddressSource.Member.Should().Be("Hauskoordinaten*.txt", "the member's name carries the edition");
        he.AddressSource.Qualities.Should().Equal("A", "B");
        he.FillMissingPlzFromPostcodeAreas.Should().BeTrue("the file has no postal columns at all");
    }

    /// <summary>
    /// Saarland's native ALKIS Hauskoordinaten WFS: flat features with their PLZ, joined to the
    /// INSPIRE parcels. ONM is the Gemeinde; the data has no separate Ortsteil level.
    /// </summary>
    [Fact]
    public void ShippedSources_SaarlandJoinsItsHauskoordinatenWfsToTheInspireParcels()
    {
        var sl = ShippedSources().Single(s => s.Source == "sl");

        sl.ParcelFeatureType.TypeName.Should().Be("cp:CadastralParcel");
        sl.AddressSource.Url.Should().Be("https://geoportal.saarland.de/registry/wfs/416");
        sl.AddressSource.TypeName.Should().Be("GDI_ALKIS_Gebaeude:Hauskoordinaten");
        sl.AddressSource.Namespace.Should().BeNull("the server resolves the prefix on its own");
        sl.AddressSource.Fields.Should().BeEquivalentTo(new AddressFieldOptions
        {
            Street = "STN", HouseNumber = "HNR", HouseNumberSuffix = "ADZ", Plz = "PLZ", Ort = "ONM", Gemeinde = "ONM",
        });
        sl.FillMissingPlzFromPostcodeAreas.Should().BeFalse("every address carries its PLZ");
    }

    [Fact]
    public void ShippedSources_InspireSourcesKeepTheCadastralParcelDefaults() =>
        ShippedSources().Where(s => s.AddressSource.Type is not (AddressSourceType.ParcelLagebezeichnung or AddressSourceType.FlatWfs))
            .Should().OnlyContain(s => s.ParcelFeatureType.TypeName == "cp:CadastralParcel" && s.ParcelFeatureType.AreaField == "areaValue");

    [Fact]
    public void ShippedSources_RheinlandPfalzReadsTheAlkisVereinfachtParcels()
    {
        var rp = ShippedSources().Single(s => s.Source == "rp");

        rp.ParcelFeatureType.TypeName.Should().Be("ave:Flurstueck");
        rp.ParcelFeatureType.AreaField.Should().Be("flaeche");
        rp.ParcelFeatureType.GemeindeField.Should().Be("gemeinde");
        rp.ParcelFeatureType.LagebezeichnungField.Should().Be("lagebeztxt");
        rp.FillMissingPlzFromPostcodeAreas.Should().BeTrue("ALKIS vereinfacht carries no PLZ");
        rp.BuildingFeatureType.Should().BeEquivalentTo(new { TypeName = "ave:GebaeudeBauwerk", Namespace = (string?)null, LagebezeichnungField = "lagebeztxt" });
    }

    [Fact]
    public void ShippedSources_ThueringenBindsTheAvePrefixAndKeepsPagesSmall()
    {
        var th = ShippedSources().Single(s => s.Source == "th");

        th.ParcelFeatureType.TypeName.Should().Be("ave:Flurstueck");
        th.ParcelFeatureType.Namespace.Should().Be(
            "http://repository.gdi-de.org/schemas/adv/produkt/alkis-vereinfacht/1.0", "the server doesn't know the prefix without it");
        th.ParcelFeatureType.LagebezeichnungField.Should().Be("lagebeztxt");
        th.PageSize.Should().Be(1000, "5,000 parcels take the server 43 s, 1,000 only 2 s");
        th.FillMissingPlzFromPostcodeAreas.Should().BeTrue("ALKIS vereinfacht carries no PLZ");
        th.BuildingFeatureType.Should().BeEquivalentTo(new
        {
            TypeName = "ave:GebaeudeBauwerk",
            Namespace = "http://repository.gdi-de.org/schemas/adv/produkt/alkis-vereinfacht/1.0",
            LagebezeichnungField = "lagebeztxt",
        });
    }

    [Fact]
    public void ShippedSources_BremenJoinsItsNativeAddressesWithTheirPlz()
    {
        var hb = ShippedSources().Single(s => s.Source == "hb");

        hb.ParcelFeatureType.TypeName.Should().Be("app:flurstuecke");
        hb.ParcelFeatureType.Namespace.Should().Be("http://www.deegree.org/app", "deegree counts nothing without it");
        hb.ParcelFeatureType.AreaField.Should().Be("flaeche");
        hb.AddressSource.TypeName.Should().Be("app:adressen");
        hb.AddressSource.Namespace.Should().Be("http://www.deegree.org/app");
        hb.AddressSource.Fields.Should().BeEquivalentTo(new AddressFieldOptions
        {
            Street = "stn", HouseNumber = "hnr", HouseNumberSuffix = "adz", Plz = "plz", Ort = "onm", Gemeinde = "onm",
        });
        hb.FillMissingPlzFromPostcodeAreas.Should().BeFalse("every address carries its PLZ");
    }

    [Fact]
    public void ShippedSources_BerlinJoinsItsAddressRegisterAsOneGemeinde()
    {
        var be = ShippedSources().Single(s => s.Source == "be");

        be.ParcelFeatureType.TypeName.Should().Be("alkis_flurstuecke:flurstuecke");
        be.ParcelFeatureType.AreaField.Should().Be("afl");
        be.AddressSource.TypeName.Should().Be("adressen_berlin:adressen_berlin");
        be.AddressSource.Fields.Should().BeEquivalentTo(new AddressFieldOptions
        {
            Street = "str_name", HouseNumber = "hnr", HouseNumberSuffix = "hnr_zusatz", Plz = "plz", Ort = "ort_name", FixedGemeinde = "Berlin",
        });
        be.CrsEpsgCode.Should().Be(25833);
    }
}
