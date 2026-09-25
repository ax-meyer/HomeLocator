using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Grundstuecksfinder.Services.Importers.Inspire.Addresses;
using NetTopologySuite.Geometries;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire.Addresses;

/// <summary>
/// The two dialects of the Hauskoordinaten export the import reads, as tiny inline copies of
/// the real files' first lines: Baden-Württemberg's (';', unquoted, LF, empty postal columns)
/// and Hessen's (',', numeric fields quoted, CRLF, no postal columns, "kreissch" typo).
/// </summary>
public sealed class HkFileReaderTests
{
    private const string BwHeader =
        "nba;oid;qua;landschl;land;regbezschl;regbez;kreisschl;kreis;gmdschl;gmd;ottschl;ott;strschl;str;hnr;adz;zone;ostwert;nordwert;postplz;postonm;postonmzus;postott;poststr;aud";

    private const string HeHeader =
        "nba,oid,qua,landschl,land,regbezschl,regbez,kreissch,kreis,gmdschl,gmd,ottschl,ott,strschl,str,hnr,adz,zone,ostwert,nordwert";

    private static string BwRow(string qua, string gmd, string str, string hnr, string adz = "", string x = "420637.510", string y = "5368155.180", string plz = "", string zone = "32") =>
        $"N;DEBWhk01000bvnHs;{qua};08;Baden-Württemberg;3;Freiburg;17;Ortenaukreis;096;{gmd};0000;;91489;{str};{hnr};{adz};{zone};{x};{y};{plz};;;;;2026-07-15";

    private static (HkReadResult Result, List<AddressFeature> Addresses) Read(
        string text, IReadOnlyCollection<string>? qualities = null, int zone = 32)
    {
        var builder = new PreloadedAddresses.Builder();
        var result = HkFileReader.Read(new StringReader(text), qualities ?? ["A", "B"], zone, builder, TestContext.Current.CancellationToken);
        var addresses = builder.Build(result.Accepted)
            .Query(new Envelope(-1e9, 1e9, -1e9, 1e9))
            .OrderBy(a => a.Location.X).ThenBy(a => a.Hnr, StringComparer.Ordinal)
            .ToList();
        return (result, addresses);
    }

    [Fact]
    public void Read_BadenWuerttembergDialect_ReadsColumnsByName()
    {
        var text = string.Join('\n',
            BwHeader,
            BwRow("A", "Offenburg", "Schwalbenweg", "106"),
            BwRow("B", "Offenburg", "Schwalbenweg", "104", adz: "a", x: "420637.910", plz: "77652"),
            "");

        var (result, addresses) = Read(text);

        result.Should().Be(new HkReadResult(Rows: 2, Accepted: 2, OtherQuality: 0, Malformed: 0));
        addresses.Should().BeEquivalentTo(
        [
            new { Str = "Schwalbenweg", Hnr = "106", HnrZus = (string?)null, Plz = (string?)null, Ort = "Offenburg", Gemeinde = "Offenburg" },
            new { Str = "Schwalbenweg", Hnr = "104", HnrZus = (string?)"a", Plz = (string?)"77652", Ort = "Offenburg", Gemeinde = "Offenburg" },
        ], o => o.WithStrictOrdering());
        addresses[0].Location.Coordinate.Should().Be(new Coordinate(420637.510, 5368155.180));
    }

    [Fact]
    public void Read_HessenDialect_UnquotesFieldsToleratesCrlfAndNeedsNoPostalColumns()
    {
        var text = string.Join("\r\n",
            HeHeader,
            """N,DEHE06180000xaUk,A,"06",Hessen,"6",Kassel,"11",Kreisfreie Stadt Kassel,"000",Kassel,"1530",Kassel,"00022",Akazienweg,"3",A,"32","534215.816","5685098.32" """.TrimEnd(),
            """N,DEHE06412000abcd,B,"06",Hessen,"4",Darmstadt,"12",Kreisfreie Stadt Frankfurt am Main,"000",Frankfurt am Main,"0045",Sachsenhausen,"01234",Mörfelder Landstraße,"12",,"32","477000.5","5550000.25" """.TrimEnd(),
            "");

        var (result, addresses) = Read(text);

        result.Accepted.Should().Be(2);
        result.Malformed.Should().Be(0);
        addresses.Should().BeEquivalentTo(
        [
            new { Str = "Mörfelder Landstraße", Hnr = "12", HnrZus = (string?)null, Plz = (string?)null, Ort = "Sachsenhausen", Gemeinde = "Frankfurt am Main" },
            new { Str = "Akazienweg", Hnr = "3", HnrZus = (string?)"A", Plz = (string?)null, Ort = "Kassel", Gemeinde = "Kassel" },
        ], o => o.WithStrictOrdering());
        addresses[0].Location.Coordinate.Should().Be(new Coordinate(477000.5, 5550000.25));
    }

    [Theory]
    [InlineData("Frankfurt Bezirk 32", "Frankfurt am Main")]
    [InlineData("Darmstadt Bezirk 5", "Darmstadt")]
    [InlineData("Sachsenhausen", "Sachsenhausen")]
    [InlineData("Bezirksweiler", "Bezirksweiler")]
    public void Read_NumberedDistrict_IsNoOrt(string ott, string expectedOrt)
    {
        // Hessen numbers some cities' districts instead of naming them; such an "Ortsteil" is no
        // place name, so Ort falls back to the Gemeinde.
        var gemeinde = expectedOrt == ott ? "Frankfurt am Main" : expectedOrt;
        var text = HeHeader + "\r\n" + string.Join(',',
            "N", "DEHE06412000abcd", "A", "\"06\"", "Hessen", "\"4\"", "Darmstadt", "\"12\"", "Kreis",
            "\"000\"", gemeinde, "\"0045\"", ott, "\"01234\"", "Zeil", "\"1\"", "", "\"32\"", "\"477000.5\"", "\"5550000.25\"");

        var (_, addresses) = Read(text);

        addresses.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Ort = expectedOrt, Gemeinde = gemeinde });
    }

    [Fact]
    public void Read_QuotedFieldWithDelimiterAndEscapedQuote_IsOneValue()
    {
        var text = HeHeader + "\n" +
                   """N,x,A,"06",Hessen,"4",Darmstadt,"12",Kreis,"000",Gemeinde,"0001",,"1","Am ""Alten"" Markt, Nord","5",,"32","1","2" """.TrimEnd();

        var (result, addresses) = Read(text);

        result.Malformed.Should().Be(0);
        addresses.Should().ContainSingle().Which.Str.Should().Be("Am \"Alten\" Markt, Nord");
    }

    [Fact]
    public void Read_QualityFilter_KeepsOnlyTheAllowedQualities()
    {
        // BW's quality C rows carry house numbers made up from the coordinate
        // ("Distr. Am Ottersberg 86188851") — not addresses anyone lives at.
        var text = string.Join('\n',
            BwHeader,
            BwRow("A", "Oppenau", "Ottersbergstraße", "12"),
            BwRow("B", "Oppenau", "Ottersbergstraße", "14"),
            BwRow("C", "Oppenau", "Distr. Am Ottersberg", "86188851"),
            BwRow("c", "Oppenau", "Distr. Am Ottersberg", "86188852"));

        var (result, addresses) = Read(text);

        result.Should().Be(new HkReadResult(Rows: 4, Accepted: 2, OtherQuality: 2, Malformed: 0));
        addresses.Select(a => a.Hnr).Should().BeEquivalentTo("12", "14");
    }

    [Fact]
    public void Read_QualityFilter_IsConfigurable()
    {
        var text = string.Join('\n', BwHeader, BwRow("A", "Oppenau", "Weg", "1"), BwRow("B", "Oppenau", "Weg", "2"));

        var (result, addresses) = Read(text, qualities: ["a"]);

        result.OtherQuality.Should().Be(1);
        addresses.Should().ContainSingle().Which.Hnr.Should().Be("1", "qualities compare case-insensitively");
    }

    [Fact]
    public void Read_PlaceNames_AreNormalizedLikeEverySourcesAre()
    {
        var text = string.Join('\n', BwHeader, BwRow("A", "Freiburg im Breisgau, Stadt", "Hauptstraße", "1"));

        var (_, addresses) = Read(text);

        addresses.Should().ContainSingle().Which.Gemeinde.Should().Be("Freiburg im Breisgau");
    }

    [Theory]
    [InlineData("N;x;A;08;BW;3;F;17;K;096;Offenburg;0000;;91489;Weg;1;;32;420637.510;5368155.180;;;;;", "one field short")]
    [InlineData("N;x;A;08;BW;3;F;17;K;096;Offenburg;0000;;91489;Weg;1;;32;420637.510;5368155.180;;;;;;2026;extra", "one field too many")]
    [InlineData("N;x;A;08;BW;3;F;17;K;096;Offenburg;0000;;91489;Weg;1;;32;;5368155.180;;;;;;2026", "no easting")]
    [InlineData("N;x;A;08;BW;3;F;17;K;096;Offenburg;0000;;91489;Weg;1;;32;420637,510;5368155.180;;;;;;2026", "decimal comma")]
    [InlineData("N;x;A;08;BW;3;F;17;K;096;Offenburg;0000;;91489;Weg;1;;32;NaN;5368155.180;;;;;;2026", "NaN easting")]
    [InlineData("N;x;A;08;BW;3;F;17;K;096;Offenburg;0000;;91489;Weg;1;;32;420637.510;Infinity;;;;;;2026", "infinite northing")]
    public void Read_UnreadableRow_IsCountedAsMalformed(string row, string because)
    {
        var text = string.Join('\n', BwHeader, BwRow("A", "Offenburg", "Weg", "2"), row);

        var (result, addresses) = Read(text);

        result.Malformed.Should().Be(1, because);
        addresses.Should().ContainSingle();
    }

    [Fact]
    public void Read_RowInAnotherUtmZone_FailsTheRead()
    {
        var text = string.Join('\n', BwHeader, BwRow("A", "Offenburg", "Weg", "1", zone: "33"));

        var act = () => Read(text);

        act.Should().Throw<InspireImportException>().WithMessage("*zone \"33\", not the source's zone 32*");
    }

    [Fact]
    public void Read_MissingRequiredColumn_FailsTheRead()
    {
        var act = () => Read(BwHeader.Replace(";hnr;", ";hausnummer;", StringComparison.Ordinal) + "\n");

        act.Should().Throw<InspireImportException>().WithMessage("*no \"hnr\" column*");
    }

    [Fact]
    public void Read_EmptyFile_FailsTheRead()
    {
        var act = () => Read("");

        act.Should().Throw<InspireImportException>().WithMessage("*empty*");
    }

    [Fact]
    public void Read_ByteOrderMarkAndBlankLines_AreIgnored()
    {
        var text = "﻿" + BwHeader + "\n\n" + BwRow("A", "Offenburg", "Weg", "1") + "\n\n";

        var (result, addresses) = Read(text);

        result.Rows.Should().Be(1);
        addresses.Should().ContainSingle();
    }

    [Theory]
    [InlineData("a;b;c", ';')]
    [InlineData("a,b,c", ',')]
    [InlineData("a\tb\tc", '\t')]
    [InlineData("\"x;y\",b,c", ',')]
    public void DetectDelimiter_TakesTheMostFrequentOutsideQuotes(string header, char expected) =>
        HkFileReader.DetectDelimiter(header).Should().Be(expected);
}
