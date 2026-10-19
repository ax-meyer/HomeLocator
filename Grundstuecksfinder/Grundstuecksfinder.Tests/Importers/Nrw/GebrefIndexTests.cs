using System.Text;
using FluentAssertions;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Services.Importers.Nrw;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Nrw;

public sealed class GebrefIndexTests
{
    /// <summary>A GEBREF line as the real file has it (21 fields, EPSG 25832).</summary>
    internal static string Line(string gemeinde, string street, string hnr, string suffix, double east, double north, string zone = "32") =>
        FormattableString.Invariant(
            $"N;DENW01HK1000000;A;05;Nordrhein-Westfalen;9;Arnsberg;11;{gemeinde};000;{gemeinde};0000;;04086;{street};{hnr};{suffix};{zone};{east};{north};2026-07-01");

    private static async Task<GebrefIndex> LoadAsync(params string[] lines) =>
        await GebrefIndex.LoadAsync(new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', lines))), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Find_ReturnsThePointInLonLat()
    {
        var index = await LoadAsync(Line("Bochum", "Obertor", "50", "", 370279.838, 5705335.366));

        var (lon, lat) = new UtmToLonLat(25832).Transform(370279.838, 5705335.366);
        index.Find("Bochum", "Obertor", "50", null).Should().Be(((float)lat, (float)lon));
    }

    [Theory]
    // GEBREF abbreviates where the Grundsteuer CSV writes the street out.
    [InlineData("Schlaraffiastr.", "Schlaraffiastraße")]
    [InlineData("Öckendorfer Str.", "Öckendorfer Straße")]
    [InlineData("Lise-Meitner-Str.", "Lise-Meitner-Straße")]
    [InlineData("Str. des 17. Juni", "Straße des 17. Juni")]
    [InlineData("Am  Sandberg", "Am Sandberg")]
    [InlineData("Lise Meitner Straße", "Lise-Meitner-Straße")]
    public async Task Find_MatchesTheCsvsSpellingOfTheStreet(string gebref, string csv)
    {
        var index = await LoadAsync(Line("Bochum", gebref, "1", "", 370000, 5700000));

        index.Find("Bochum", csv, "1", null).Should().NotBeNull();
    }

    [Fact]
    public async Task Find_StrInsideAWord_IsNotExpanded()
    {
        // Only "str." ending a word is an abbreviation; "Strunde" stays "strunde".
        var index = await LoadAsync(Line("Köln", "An der Strunde", "3", "", 370000, 5700000));

        index.Find("Köln", "An der Strassende", "3", null).Should().BeNull();
        index.Find("Köln", "An der Strunde", "3", null).Should().NotBeNull();
    }

    [Theory]
    [InlineData("a", "A")]
    [InlineData("a", " a")]
    [InlineData("", null)]
    public async Task Find_IgnoresCaseAndSpacingOfTheSuffix(string gebref, string? csv)
    {
        var index = await LoadAsync(Line("Bochum", "Wilhelm-Leithe-Weg", "76", gebref, 370000, 5700000));

        index.Find("Bochum", "Wilhelm-Leithe-Weg", "76", csv).Should().NotBeNull();
    }

    [Fact]
    public async Task Find_OtherNumberOrSuffix_IsNotFound()
    {
        var index = await LoadAsync(Line("Bochum", "Obertor", "50", "", 370000, 5700000));

        index.Find("Bochum", "Obertor", "5", null).Should().BeNull();
        index.Find("Bochum", "Obertor", "50", "a").Should().BeNull();
        index.Find("Herne", "Obertor", "50", null).Should().BeNull();
    }

    [Fact]
    public async Task Load_AddressAtTwoPlaces_IsDropped()
    {
        // The same street name in two Ortsteile of one Gemeinde: no single answer.
        var index = await LoadAsync(
            Line("Bochum", "Hauptstr.", "1", "", 370000, 5700000),
            Line("Bochum", "Hauptstr.", "1", "", 375000, 5704000),
            Line("Bochum", "Hauptstr.", "2", "", 370010, 5700000));

        index.Find("Bochum", "Hauptstraße", "1", null).Should().BeNull();
        index.Find("Bochum", "Hauptstraße", "2", null).Should().NotBeNull();
        index.Ambiguous.Should().Be(1);
        index.Count.Should().Be(1);
    }

    [Fact]
    public async Task Load_AddressListedTwiceAtOnePoint_IsKept()
    {
        var index = await LoadAsync(
            Line("Bochum", "Obertor", "50", "", 370279.838, 5705335.366),
            Line("Bochum", "Obertor", "50", "", 370279.838, 5705335.366));

        index.Find("Bochum", "Obertor", "50", null).Should().NotBeNull();
        index.Ambiguous.Should().Be(0);
    }

    [Fact]
    public async Task Load_LinesThatAreNoAddressInZone32_AreSkipped()
    {
        var index = await LoadAsync(
            Line("Bochum", "Obertor", "50", "", 370000, 5700000),
            Line("Bochum", "Obertor", "52", "", 370000, 5700000),
            Line("Bochum", "Obertor", "54", "", 370000, 5700000, zone: "33"),
            "N;too;short");

        index.Count.Should().Be(2);
        index.Skipped.Should().Be(2);
    }

    [Fact]
    public async Task Load_MostlyUnreadable_Fails()
    {
        var act = () => LoadAsync(Line("Bochum", "Obertor", "50", "", 370000, 5700000), "a;b", "c;d");

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*format*");
    }
}
