using FluentAssertions;
using Grundstuecksfinder.Services;
using Xunit;

namespace Grundstuecksfinder.Tests;

public class CsvParserTests
{
    private static string ValidLine(
        string id = "1", string alkisoid = "X", string flstkennz = "Y",
        string lagebeztext = "NULL",
        string str = "Musterstraße", string hnr = "42", string hnrZus = "a",
        string plz = "50667", string ort = "Köln", string gemeinde = "Köln",
        string flaeche = "450.0")
    {
        // 44 columns; only indices 4-9 and 16 matter for parsing
        var cols = new string[44];
        for (var i = 0; i < cols.Length; i++) cols[i] = "NULL";
        cols[0] = id; cols[1] = alkisoid; cols[2] = flstkennz; cols[3] = lagebeztext;
        cols[4] = str; cols[5] = hnr; cols[6] = hnrZus;
        cols[7] = plz; cols[8] = ort; cols[9] = gemeinde;
        cols[16] = flaeche;
        return string.Join(";", cols);
    }

    [Fact]
    public void ParseLine_ValidEntry_ReturnsProperty()
    {
        var property = CsvParser.ParseLine(ValidLine());

        property.Should().NotBeNull();
        property!.Str.Should().Be("Musterstraße");
        property.Hnr.Should().Be("42");
        property.HnrZus.Should().Be("a");
        property.Plz.Should().Be("50667");
        property.Ort.Should().Be("Köln");
        property.Gemeinde.Should().Be("Köln");
        property.FlaecheAmtl.Should().Be(450.0);
    }

    [Fact]
    public void ParseLine_NullStreet_ReturnsNull()
    {
        CsvParser.ParseLine(ValidLine(str: "NULL")).Should().BeNull();
    }

    [Fact]
    public void ParseLine_NullHnr_ReturnsNull()
    {
        CsvParser.ParseLine(ValidLine(hnr: "NULL")).Should().BeNull();
    }

    [Fact]
    public void ParseLine_NullStreetAndNullHnr_ReturnsNull()
    {
        CsvParser.ParseLine(ValidLine(str: "NULL", hnr: "NULL")).Should().BeNull();
    }

    [Fact]
    public void ParseLine_EmptyLine_ReturnsNull()
    {
        CsvParser.ParseLine("").Should().BeNull();
        CsvParser.ParseLine("   ").Should().BeNull();
    }

    [Fact]
    public void ParseLine_TooFewColumns_ReturnsNull()
    {
        CsvParser.ParseLine("1;2;3;4;5").Should().BeNull();
    }

    [Fact]
    public void ParseLine_InvalidFlaeche_SetsNull()
    {
        var property = CsvParser.ParseLine(ValidLine(flaeche: "not-a-number"));

        property.Should().NotBeNull();
        property!.FlaecheAmtl.Should().BeNull();
    }

    [Fact]
    public void ParseLine_TrimsWhitespace()
    {
        var property = CsvParser.ParseLine(ValidLine(str: "  Bergstraße  ", plz: " 44139 "));

        property.Should().NotBeNull();
        property!.Str.Should().Be("Bergstraße");
        property.Plz.Should().Be("44139");
    }
}
