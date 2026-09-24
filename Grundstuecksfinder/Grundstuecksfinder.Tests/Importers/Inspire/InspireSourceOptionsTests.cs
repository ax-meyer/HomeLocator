using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Grundstuecksfinder.Services.Importers.Inspire.Addresses;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public sealed class InspireSourceOptionsTests
{
    private static InspireSourceOptions Valid(string source = "sh") => new()
    {
        Source = source,
        ParcelWfsUrl = "https://example.org/cp",
        AddressSource = new AddressSourceOptions { Url = "https://example.org/ad" },
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
        broken.MaxFailedTiles = -1;
        broken.MinRequestIntervalSeconds = -1;
        broken.MinCompleteness = 1.5;
        broken.MinPostcodeFillRatio = -0.1;

        var errors = InspireSourceOptions.Validate([broken], []);

        errors.Should().HaveCount(9);
    }

    [Fact]
    public void Validate_AddressSourceWithoutUrl_IsRejected()
    {
        var broken = Valid();
        broken.AddressSource.Url = "";

        var errors = InspireSourceOptions.Validate([broken], []);

        errors.Should().ContainSingle().Which.Should().Be("sh: AddressSource.Url must be an absolute http(s) URL.");
    }

    [Fact]
    public void Validate_UndefinedAddressSourceType_IsRejected()
    {
        // The config binder accepts a number for an enum, so "Type": "7" would bind.
        var broken = Valid();
        broken.AddressSource.Type = (AddressSourceType)7;

        var errors = InspireSourceOptions.Validate([broken], []);

        errors.Should().ContainSingle(e => e.Contains("AddressSource.Type must be one of"));
    }

    [Fact]
    public void Validate_OgcApiFeaturesWithNonPositivePageSize_IsRejected()
    {
        var broken = Valid();
        broken.AddressSource.Type = AddressSourceType.OgcApiFeatures;
        broken.AddressSource.OgcApiPageSize = 0;

        var errors = InspireSourceOptions.Validate([broken], []);

        errors.Should().ContainSingle(e => e.Contains("AddressSource.OgcApiPageSize"));
    }

    private static InspireSourceOptions ValidHkFile()
    {
        var options = Valid("bw");
        options.AddressSource = new AddressSourceOptions
        {
            Type = AddressSourceType.HkFile,
            Url = "https://example.org/hk_bw.zip",
            Member = "adressen-bw.txt",
        };
        return options;
    }

    [Fact]
    public void Validate_ValidHkFile_HasNoErrors() =>
        InspireSourceOptions.Validate([ValidHkFile()], []).Should().BeEmpty();

    [Fact]
    public void Validate_BrokenHkFile_IsAllReported()
    {
        var broken = ValidHkFile();
        broken.AddressSource.Member = " ";
        broken.AddressSource.AllowedQualities = ["A", ""];
        broken.AddressSource.DownloadTimeoutSeconds = 0;
        broken.AddressSource.Locator = (HkFileLocatorType)9;

        var errors = InspireSourceOptions.Validate([broken], []);

        errors.Should().BeEquivalentTo(
            "bw: AddressSource.Locator must be one of StaticUrl, HessenDownloadCenter.",
            "bw: AddressSource.Member must name the ZIP entry to read.",
            "bw: AddressSource.AllowedQualities must list at least one quality, and no blank ones.",
            "bw: AddressSource.DownloadTimeoutSeconds must be greater than 0 and at most 86400.");
    }

    [Theory]
    [InlineData(86_401)]
    [InlineData(double.MaxValue)] // would overflow TimeSpan.FromSeconds at the first download
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void Validate_DownloadTimeoutOutOfRange_IsRejected(double seconds)
    {
        var broken = ValidHkFile();
        broken.AddressSource.DownloadTimeoutSeconds = seconds;

        InspireSourceOptions.Validate([broken], []).Should().ContainSingle(e => e.Contains("DownloadTimeoutSeconds"));
    }

    [Fact]
    public void Validate_LongestDownloadTimeout_IsAccepted()
    {
        var options = ValidHkFile();
        options.AddressSource.DownloadTimeoutSeconds = AddressSourceOptions.MaxDownloadTimeoutSeconds;

        InspireSourceOptions.Validate([options], []).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_001)]
    public void Validate_OgcApiPageSizeOutOfRange_IsRejected(int pageSize)
    {
        var broken = Valid();
        broken.AddressSource.Type = AddressSourceType.OgcApiFeatures;
        broken.AddressSource.OgcApiPageSize = pageSize;

        InspireSourceOptions.Validate([broken], []).Should().ContainSingle()
            .Which.Should().Be("sh: AddressSource.OgcApiPageSize must be between 1 and 10000.");
    }

    [Fact]
    public void Validate_EmptyAllowedQualities_IsRejected()
    {
        var broken = ValidHkFile();
        broken.AddressSource.AllowedQualities = [];

        InspireSourceOptions.Validate([broken], []).Should().ContainSingle(e => e.Contains("AllowedQualities"));
    }

    [Fact]
    public void AllowedQualities_Unset_DefaultsToTheBuildingPlacedOnes() =>
        new AddressSourceOptions().Qualities.Should().Equal("A", "B");

    [Fact]
    public void AllowedQualities_Configured_ReplaceTheDefaultInsteadOfAddingToIt()
    {
        // The binder appends a configured list to an initialised one; ["A"] must stay ["A"].
        var options = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AddressSource:AllowedQualities:0"] = "A" })
            .Build()
            .Get<InspireSourceOptions>()!;

        options.AddressSource.Qualities.Should().Equal("A");
    }
}
