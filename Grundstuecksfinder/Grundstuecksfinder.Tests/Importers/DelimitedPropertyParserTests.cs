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

    [Fact]
    public void ParseLine_InvalidPlz_SetsToNull()
    {
        var property = DelimitedPropertyParser.ParseLine("Bahnhofstraße,7,b,.78,Beispielstadt,Beispielgemeinde,500.5", Map);

        property.Should().NotBeNull();
        property!.Plz.Should().BeNull("PLZ '.78' is not a valid 5-digit code");
    }

    [Fact]
    public void ParseLine_InvalidGemeinde_SetsToNull()
    {
        var property = DelimitedPropertyParser.ParseLine(
            "Bahnhofstraße,7,b,12345,Beispielstadt,\"[\"\"053412001858A\"\"]\",500.5", Map);

        property.Should().NotBeNull();
        property!.Gemeinde.Should().BeNull("Gemeinde value starting with '[' is not a valid name");
    }

    // ── Quoted-field splitting ──────────────────────────────────────────

    private static readonly CsvColumnMap SemiMap = new(
        Delimiter: ';',
        MinColumnCount: 7,
        StrIndex: 0,
        HnrIndex: 1,
        HnrZusIndex: 2,
        PlzIndex: 3,
        OrtIndex: 4,
        GemeindeIndex: 5,
        FlaecheAmtlIndex: 6,
        NullToken: "NULL");

    [Fact]
    public void SplitCsvLine_SemicolonInsideQuotedField_NotTreatedAsDelimiter()
    {
        // 8 semicolons visible, but 2 are inside quotes — should yield 7 fields
        var line = "Hauptstr;10;NULL;50667;Köln;Köln;\"extra;data\"";
        var parts = DelimitedPropertyParser.SplitCsvLine(line, ';');

        parts.Should().HaveCount(7);
        parts[5].Should().Be("Köln");
        parts[6].Should().Be("extra;data");
    }

    [Fact]
    public void SplitCsvLine_EscapedQuotesInsideQuotedField()
    {
        var line = "Str;1;NULL;50667;Köln;Köln;\"he said \"\"hello\"\"\"";
        var parts = DelimitedPropertyParser.SplitCsvLine(line, ';');

        parts.Should().HaveCount(7);
        parts[6].Should().Be("he said \"hello\"");
    }

    [Fact]
    public void ParseLine_QuotedFieldWithDelimiter_ColumnsStayAligned()
    {
        // Simulates NRW's bodenrichtwerte column containing semicolons inside quotes
        var line = "Bahnhofstraße;7;NULL;50667;Köln;Köln;500.5;\"json;with;semicolons\"";
        var map = new CsvColumnMap(
            Delimiter: ';',
            MinColumnCount: 7,
            StrIndex: 0, HnrIndex: 1, HnrZusIndex: 2,
            PlzIndex: 3, OrtIndex: 4, GemeindeIndex: 5,
            FlaecheAmtlIndex: 6,
            NullToken: "NULL");

        var property = DelimitedPropertyParser.ParseLine(line, map);

        property.Should().NotBeNull();
        property!.Plz.Should().Be("50667");
        property.Gemeinde.Should().Be("Köln");
        property.FlaecheAmtl.Should().Be(500.5);
    }
}
