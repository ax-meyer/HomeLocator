using FluentAssertions;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Services.Importers.Nrw;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Nrw;

public class NrwColumnMapParsingTests
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

    private static Grundstuecksfinder.Models.Property? Parse(string line) =>
        DelimitedPropertyParser.ParseLine(line, NrwPropertyImporter.ColumnMap);

    [Fact]
    public void ParseLine_ValidEntry_ReturnsProperty()
    {
        var property = Parse(ValidLine());

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
        Parse(ValidLine(str: "NULL")).Should().BeNull();
    }

    [Fact]
    public void ParseLine_NullHnr_ReturnsNull()
    {
        Parse(ValidLine(hnr: "NULL")).Should().BeNull();
    }

    [Fact]
    public void ParseLine_NullStreetAndNullHnr_ReturnsNull()
    {
        Parse(ValidLine(str: "NULL", hnr: "NULL")).Should().BeNull();
    }

    [Fact]
    public void ParseLine_EmptyLine_ReturnsNull()
    {
        Parse("").Should().BeNull();
        Parse("   ").Should().BeNull();
    }

    [Fact]
    public void ParseLine_TooFewColumns_ReturnsNull()
    {
        Parse("1;2;3;4;5").Should().BeNull();
    }

    [Fact]
    public void ParseLine_InvalidFlaeche_SetsNull()
    {
        var property = Parse(ValidLine(flaeche: "not-a-number"));

        property.Should().NotBeNull();
        property!.FlaecheAmtl.Should().BeNull();
    }

    [Fact]
    public void ParseLine_TrimsWhitespace()
    {
        var property = Parse(ValidLine(str: "  Bergstraße  ", plz: " 44139 "));

        property.Should().NotBeNull();
        property!.Str.Should().Be("Bergstraße");
        property.Plz.Should().Be("44139");
    }

    [Fact]
    public void ParseLine_QuotedSemicolonInLagebeztext_DoesNotShiftAddressColumns()
    {
        var cols = new string[44];
        for (var i = 0; i < cols.Length; i++) cols[i] = "NULL";
        cols[0] = "9375677";
        cols[3] = "\"Brockbieke; Up'n Esch\"";
        cols[4] = "Musterstraße";
        cols[5] = "42";
        cols[7] = "50667";
        cols[8] = "Köln";
        cols[9] = "Köln";
        cols[16] = "450.0";

        var property = Parse(string.Join(";", cols));

        property.Should().NotBeNull();
        property!.Str.Should().Be("Musterstraße");
        property.Hnr.Should().Be("42");
        property.Plz.Should().Be("50667");
        property.Ort.Should().Be("Köln");
        property.Gemeinde.Should().Be("Köln");
    }

    [Fact]
    public void ParseLine_QuotedLagebeztextWithoutAddress_IsRejected()
    {
        var cols = new string[44];
        for (var i = 0; i < cols.Length; i++) cols[i] = "NULL";
        cols[0] = "9375677";
        cols[3] = "\"Brockbieke; Up'n Esch\"";
        cols[9] = "Lienen";
        cols[10] = "Lienen";
        cols[11] = "055047";

        Parse(string.Join(";", cols)).Should().BeNull();
    }
}
