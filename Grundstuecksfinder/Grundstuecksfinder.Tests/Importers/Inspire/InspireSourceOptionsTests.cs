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
    public void Validate_Defaults_AreValid() =>
        InspireSourceOptions.Validate([Valid()], []).Should().BeEmpty();

    /// <summary>Sets one option of an otherwise valid source.</summary>
    private static readonly Dictionary<string, Action<InspireSourceOptions, double>> Setters = new()
    {
        ["RequestTimeoutSeconds"] = (o, v) => o.RequestTimeoutSeconds = v,
        ["MinRequestIntervalSeconds"] = (o, v) => o.MinRequestIntervalSeconds = v,
        ["RetryBaseDelaySeconds"] = (o, v) => o.RetryBaseDelaySeconds = v,
        ["MaxRetryDelaySeconds"] = (o, v) => o.MaxRetryDelaySeconds = v,
        ["CircuitSamplingSeconds"] = (o, v) => o.CircuitSamplingSeconds = v,
        ["CircuitBreakSeconds"] = (o, v) => o.CircuitBreakSeconds = v,
        ["CircuitFailureRatio"] = (o, v) => o.CircuitFailureRatio = v,
        ["MinCompleteness"] = (o, v) => o.MinCompleteness = v,
        ["MaxUnmatchedRatio"] = (o, v) => o.MaxUnmatchedRatio = v,
        ["MinPostcodeFillRatio"] = (o, v) => o.MinPostcodeFillRatio = v,
        ["TileSizeMeters"] = (o, v) => o.TileSizeMeters = v,
        ["MinTileSizeMeters"] = (o, v) => o.MinTileSizeMeters = v,
        ["BoundingBox.MaxX"] = (o, v) => o.BoundingBox.MaxX = v,
    };

    [Theory]
    // Every seconds option: overflowing TimeSpan.FromSeconds, infinite, NaN, or just past the day.
    [InlineData("RequestTimeoutSeconds", double.MaxValue)]
    [InlineData("RequestTimeoutSeconds", double.PositiveInfinity)]
    [InlineData("RequestTimeoutSeconds", double.NaN)]
    [InlineData("RequestTimeoutSeconds", 86_400.5)]
    [InlineData("RequestTimeoutSeconds", 0)]
    [InlineData("MinRequestIntervalSeconds", double.MaxValue)]
    [InlineData("MinRequestIntervalSeconds", double.NaN)]
    [InlineData("MinRequestIntervalSeconds", 86_400.5)]
    [InlineData("MinRequestIntervalSeconds", -1)]
    [InlineData("RetryBaseDelaySeconds", double.NaN)]
    [InlineData("RetryBaseDelaySeconds", -1)]
    [InlineData("MaxRetryDelaySeconds", double.MaxValue)]
    [InlineData("MaxRetryDelaySeconds", double.PositiveInfinity)]
    [InlineData("MaxRetryDelaySeconds", double.NaN)]
    [InlineData("MaxRetryDelaySeconds", 86_400.5)]
    [InlineData("MaxRetryDelaySeconds", 5)] // below RetryBaseDelaySeconds' default of 10
    [InlineData("CircuitSamplingSeconds", double.PositiveInfinity)]
    [InlineData("CircuitSamplingSeconds", double.NaN)]
    [InlineData("CircuitSamplingSeconds", 0.4)]
    [InlineData("CircuitSamplingSeconds", 86_400.5)]
    [InlineData("CircuitBreakSeconds", double.MaxValue)]
    [InlineData("CircuitBreakSeconds", double.NaN)]
    [InlineData("CircuitBreakSeconds", 0.4)] // Polly's minimum break is half a second
    [InlineData("CircuitBreakSeconds", 86_400.5)]
    // Ratios: NaN slipped through the old "< 0 or > 1" checks.
    [InlineData("CircuitFailureRatio", double.NaN)]
    [InlineData("CircuitFailureRatio", 0)]
    [InlineData("MinCompleteness", double.NaN)]
    [InlineData("MaxUnmatchedRatio", double.NaN)]
    [InlineData("MinPostcodeFillRatio", double.NaN)]
    // Tiles and the bounding box: an infinite one would never finish tiling.
    [InlineData("TileSizeMeters", double.PositiveInfinity)]
    [InlineData("TileSizeMeters", double.NaN)]
    [InlineData("TileSizeMeters", 1_000_001)]
    [InlineData("MinTileSizeMeters", double.NaN)]
    [InlineData("MinTileSizeMeters", 0)]
    [InlineData("BoundingBox.MaxX", double.PositiveInfinity)]
    [InlineData("BoundingBox.MaxX", double.NaN)]
    public void Validate_OptionOutOfRange_IsRejected(string option, double value)
    {
        var broken = Valid();
        Setters[option](broken, value);

        InspireSourceOptions.Validate([broken], []).Should().ContainSingle();
    }

    [Theory]
    [InlineData("RequestTimeoutSeconds", 86_400)]
    [InlineData("MinRequestIntervalSeconds", 0)]
    [InlineData("MinRequestIntervalSeconds", 86_400)]
    [InlineData("MaxRetryDelaySeconds", 86_400)]
    [InlineData("RetryBaseDelaySeconds", 0)]
    [InlineData("CircuitSamplingSeconds", 0.5)]
    [InlineData("CircuitSamplingSeconds", 86_400)]
    [InlineData("CircuitBreakSeconds", 0.5)]
    [InlineData("CircuitBreakSeconds", 86_400)]
    [InlineData("CircuitFailureRatio", 1)]
    [InlineData("TileSizeMeters", 1_000_000)]
    public void Validate_OptionAtItsBoundary_IsAccepted(string option, double value)
    {
        var options = Valid();
        Setters[option](options, value);

        InspireSourceOptions.Validate([options], []).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 10, 10)]      // PageSize too small
    [InlineData(100_001, 10, 10)]
    [InlineData(5000, 0, 10)]    // MaxAttempts too small
    [InlineData(5000, 101, 10)]
    [InlineData(5000, 10, -1)]   // MaxFailedTiles negative
    [InlineData(5000, 10, 10_001)]
    public void Validate_CountOutOfRange_IsRejected(int pageSize, int maxAttempts, int maxFailedTiles)
    {
        var broken = Valid();
        broken.PageSize = pageSize;
        broken.MaxAttempts = maxAttempts;
        broken.MaxFailedTiles = maxFailedTiles;

        InspireSourceOptions.Validate([broken], []).Should().ContainSingle();
    }

    [Fact]
    public void Validate_CountsAtTheirBoundaries_AreAccepted()
    {
        var options = Valid();
        options.PageSize = InspireSourceOptions.MaxPageSize;
        options.MaxAttempts = InspireSourceOptions.MaxMaxAttempts;
        options.MaxFailedTiles = InspireSourceOptions.MaxMaxFailedTiles;

        InspireSourceOptions.Validate([options], []).Should().BeEmpty();
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
