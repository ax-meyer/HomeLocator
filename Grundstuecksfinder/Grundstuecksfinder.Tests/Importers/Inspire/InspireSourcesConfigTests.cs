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
    [InlineData("he", AddressSourceType.InspireWfs)]
    [InlineData("sl", AddressSourceType.OgcApiFeatures)]
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
    public void ShippedSources_SaarlandKeepsItsOgcApiPageSize() =>
        ShippedSources().Single(s => s.Source == "sl").AddressSource.OgcApiPageSize.Should().Be(2500);
}
