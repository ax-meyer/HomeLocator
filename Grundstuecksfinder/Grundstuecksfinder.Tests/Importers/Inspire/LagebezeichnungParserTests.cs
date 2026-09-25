using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

/// <summary>The lagebeztxt forms seen live in RP, TH, ST and HB (2026-09-25).</summary>
public sealed class LagebezeichnungParserTests
{
    private static LagebezeichnungAddress A(string str, string hnr, string? zus = null) => new(str, hnr, zus);

    [Fact]
    public void Parse_StreetAndNumber() =>
        LagebezeichnungParser.Parse("Fritz-Noack-Straße 20").Addresses.Should().Equal(A("Fritz-Noack-Straße", "20"));

    [Fact]
    public void Parse_SeveralNumbersOnOneStreet() =>
        LagebezeichnungParser.Parse("Turmschanzenstraße 7, 8, 9").Addresses
            .Should().Equal(A("Turmschanzenstraße", "7"), A("Turmschanzenstraße", "8"), A("Turmschanzenstraße", "9"));

    [Fact]
    public void Parse_CommaWithoutSpace() =>
        LagebezeichnungParser.Parse("Fritz-Noack-Straße 2,1").Addresses
            .Should().Equal(A("Fritz-Noack-Straße", "2"), A("Fritz-Noack-Straße", "1"));

    [Fact]
    public void Parse_SeveralStreets() =>
        LagebezeichnungParser.Parse("Arkonastraße 2; Zollstraße 15, 16").Addresses
            .Should().Equal(A("Arkonastraße", "2"), A("Zollstraße", "15"), A("Zollstraße", "16"));

    [Theory]
    [InlineData("Brückstraße 3 A", "3", "A")]
    [InlineData("Hospitalplatz 15a", "15", "a")]
    public void Parse_HouseNumberSuffix(string text, string hnr, string zus) =>
        LagebezeichnungParser.Parse(text).Addresses.Single().Should().Be(A(text[..text.IndexOf(' ', StringComparison.Ordinal)], hnr, zus));

    [Fact]
    public void Parse_NumberAndSuffixInOneList() =>
        LagebezeichnungParser.Parse("Hintere Christofsgasse 3, 3 A").Addresses
            .Should().Equal(A("Hintere Christofsgasse", "3"), A("Hintere Christofsgasse", "3", "A"));

    [Fact]
    public void Parse_StreetNameWithANumberInIt() =>
        LagebezeichnungParser.Parse("Straße des 17. Juni 5").Addresses.Should().Equal(A("Straße des 17. Juni", "5"));

    [Fact]
    public void Parse_StreetsWithoutNumbers_NameNoAddress()
    {
        var text = LagebezeichnungParser.Parse("Kleingartenanlage; Turmschanzenstraße");

        text.Addresses.Should().BeEmpty();
        text.UnreadParts.Should().Be(0, "a street the parcel only lies on is no failure to read");
    }

    [Fact]
    public void Parse_OnlyTheNumberedPartsOfAMixedText() =>
        LagebezeichnungParser.Parse("Kirche St. Petri; Neustädter Straße 4").Addresses.Should().Equal(A("Neustädter Straße", "4"));

    [Theory]
    [InlineData("Kurze 5 Morgen")]
    [InlineData("Ablaßlache 1.Gewanne")]
    [InlineData("Kreisstraße von Harbke zur K1656")]
    [InlineData("Hofgasse 1 /2")]
    [InlineData("Auf dem Loh 3 -17")]
    public void Parse_UnreadableNumbers_AreCountedNotGuessed(string text)
    {
        var parsed = LagebezeichnungParser.Parse(text);

        parsed.Addresses.Should().BeEmpty();
        parsed.UnreadParts.Should().Be(1);
    }

    [Theory]
    [InlineData("L 412")]
    [InlineData("B 40")]
    [InlineData("K 49; A 61")]
    public void Parse_RoadNumbers_NameNoAddress_AndAreNoFailure(string text)
    {
        var parsed = LagebezeichnungParser.Parse(text);

        parsed.Addresses.Should().BeEmpty();
        parsed.UnreadParts.Should().Be(0);
    }

    [Fact]
    public void Parse_RepeatedAddress_IsKeptOnce() =>
        LagebezeichnungParser.Parse("Am Charlottentor 2; Am Charlottentor 2").Addresses.Should().Equal(A("Am Charlottentor", "2"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Parse_Nothing(string? text) =>
        LagebezeichnungParser.Parse(text).Should().Be(LagebezeichnungText.Empty);
}
