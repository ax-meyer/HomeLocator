using FluentAssertions;
using Grundstuecksfinder.Services;
using Xunit;

namespace Grundstuecksfinder.Tests;

public class PlaceNameNormalizerTests
{
    [Theory]
    [InlineData("Stadt Pirna", "Pirna")]
    [InlineData("Gemeinde Bahretal", "Bahretal")]
    [InlineData("Stadt Stadt Wehlen", "Stadt Wehlen")]
    [InlineData("Kiel, Landeshauptstadt", "Kiel")]
    [InlineData("Lübeck, Hansestadt", "Lübeck")]
    [InlineData("Flensburg, Stadt", "Flensburg")]
    [InlineData("Garding, Kirchspiel", "Garding, Kirchspiel")]
    [InlineData("Cottbus [Chóśebuz]", "Cottbus")]
    [InlineData("Burg (Spreewald) [Bórkowy (Błota)]", "Burg (Spreewald)")]
    // Niedersachsen's municipal titles, which are compounds rather than a fixed handful.
    [InlineData("Adelebsen, Flecken", "Adelebsen")]
    [InlineData("Bodenwerder, Münchhausenstadt", "Bodenwerder")]
    [InlineData("Clausthal-Zellerfeld, Berg- und Universitätsstadt", "Clausthal-Zellerfeld")]
    [InlineData("Juist, Inselgemeinde", "Juist")]
    [InlineData("Loccum, Klosterflecken", "Loccum")]
    [InlineData("Wangerooge, Nordseebad", "Wangerooge")]
    [InlineData("Amelungsborn, Klostergemeinde", "Amelungsborn")]
    // A title is only a trailing one after a comma; these are the names themselves.
    [InlineData("Bad Harzburg", "Bad Harzburg")]
    [InlineData("Neustadt am Rübenberge", "Neustadt am Rübenberge")]
    [InlineData("Stadtallendorf", "Stadtallendorf")]
    [InlineData("Frankfurt am Main", "Frankfurt am Main")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void NormalizePlace(string? input, string? expected) =>
        PlaceNameNormalizer.NormalizePlace(input).Should().Be(expected);

    [Theory]
    [InlineData("Adam-Riese-Stra\uFFFDe", "Adam-Riese-Straße")]
    [InlineData("Buchbornstra\uFFFDe", "Buchbornstraße")]
    [InlineData("Stra\uFFFDe des 17. Juni", "Straße des 17. Juni")]
    [InlineData("Am M\uFFFDhlgraben", "Am M\uFFFDhlgraben")]
    [InlineData("Hauptstraße", "Hauptstraße")]
    [InlineData(" ", null)]
    public void RepairStreet(string? input, string? expected) =>
        PlaceNameNormalizer.RepairStreet(input).Should().Be(expected);
}
