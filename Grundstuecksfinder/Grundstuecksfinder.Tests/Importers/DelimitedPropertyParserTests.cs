using FluentAssertions;
using Grundstuecksfinder.Services.Importers;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers;

/// <summary>
/// Proves DelimitedPropertyParser is driven entirely by the supplied CsvColumnMap, not
/// hardcoded to NRW's layout — a differently-shaped source is just a different map.
/// </summary>
public class DelimitedPropertyParserTests
{
    // A fictitious 7-column, comma-delimited, "-" for null layout unrelated to NRW's.
    private static readonly CsvColumnMap Map = new(
        Delimiter: ',',
        MinColumnCount: 7,
        StrIndex: 0,
        HnrIndex: 1,
        HnrZusIndex: 2,
        PlzIndex: 3,
        OrtIndex: 4,
        GemeindeIndex: 5,
        FlaecheAmtlIndex: 6,
        NullToken: "-");

    [Fact]
    public void ParseLine_HonorsDelimiterAndIndices()
    {
        var property = DelimitedPropertyParser.ParseLine("Bahnhofstraße,7,b,12345,Beispielstadt,Beispielgemeinde,500.5", Map);

        property.Should().NotBeNull();
        property!.Str.Should().Be("Bahnhofstraße");
        property.Hnr.Should().Be("7");
        property.HnrZus.Should().Be("b");
        property.Plz.Should().Be("12345");
        property.Ort.Should().Be("Beispielstadt");
        property.Gemeinde.Should().Be("Beispielgemeinde");
        property.FlaecheAmtl.Should().Be(500.5);
    }

    [Fact]
    public void ParseLine_HonorsCustomNullToken()
    {
        var property = DelimitedPropertyParser.ParseLine("Bahnhofstraße,7,-,12345,Beispielstadt,Beispielgemeinde,500.5", Map);

        property.Should().NotBeNull();
        property!.HnrZus.Should().BeNull();
    }

    [Fact]
    public void ParseLine_FewerColumnsThanMinColumnCount_ReturnsNull()
    {
        DelimitedPropertyParser.ParseLine("a,b,c", Map).Should().BeNull();
    }

    [Fact]
    public void ParseLine_MissingStreetOrHnr_ReturnsNull()
    {
        DelimitedPropertyParser.ParseLine("-,7,b,12345,Beispielstadt,Beispielgemeinde,500.5", Map).Should().BeNull();
        DelimitedPropertyParser.ParseLine("Bahnhofstraße,-,b,12345,Beispielstadt,Beispielgemeinde,500.5", Map).Should().BeNull();
    }
}
