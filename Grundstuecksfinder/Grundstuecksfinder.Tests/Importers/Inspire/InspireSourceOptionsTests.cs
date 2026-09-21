using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public sealed class InspireSourceOptionsTests
{
    private static InspireSourceOptions Valid(string source = "sh") => new()
    {
        Source = source,
        DatasetName = $"{source}-alkis",
        ParcelWfsUrl = "https://example.org/cp",
        AddressWfsUrl = "https://example.org/ad",
        Crs = "http://www.opengis.net/def/crs/epsg/0/25832",
        BoundingBox = new InspireBoundingBox { MinX = 0, MinY = 0, MaxX = 10, MaxY = 10 },
    };

    [Theory]
    [InlineData("urn:ogc:def:crs:EPSG::25832", 25832)]
    [InlineData("http://www.opengis.net/def/crs/epsg/0/25833", 25833)]
    [InlineData("EPSG:4258", 4258)]
    [InlineData("urn:adv:crs:ETRS89_UTM32", 25832)]
    [InlineData("urn:adv:crs:ETRS89_UTM33*DE_DHHN2016_NH", 25833)]
    [InlineData("urn:ogc:def:crs:OGC:1.3:CRS84", null)]
    [InlineData("", null)]
    public void ParseEpsgCode_UnderstandsEverySrsNameForm(string srsName, int? expected) =>
        InspireSourceOptions.ParseEpsgCode(srsName).Should().Be(expected);

    [Fact]
    public void Validate_ValidConfig_HasNoErrors() =>
        InspireSourceOptions.Validate([Valid("sh"), Valid("bw")], ["nrw"]).Should().BeEmpty();

    [Fact]
    public void Validate_DuplicateOrReservedSource_IsRejected()
    {
        // Each source's import replaces all rows with its Source, so two entries sharing one
        // would keep wiping each other's data.
        var errors = InspireSourceOptions.Validate([Valid("sh"), Valid("sh"), Valid("nrw")], ["nrw"]);

        errors.Should().HaveCount(2).And.OnlyContain(e => e.Contains("used twice"));
    }

    [Fact]
    public void Validate_BrokenValues_AreAllReported()
    {
        var broken = Valid();
        broken.Source = "Schleswig Holstein";
        broken.ParcelWfsUrl = "not a url";
        broken.Crs = "urn:ogc:def:crs:EPSG::4258"; // geographic: tiling in metres would be wrong
        broken.BoundingBox = new InspireBoundingBox { MinX = 10, MaxX = 0, MinY = 0, MaxY = 10 };
        broken.TileSizeMeters = 0;
        broken.MinCompleteness = 1.5;

        var errors = InspireSourceOptions.Validate([broken], []);

        errors.Should().HaveCount(6);
    }
}
