using FluentAssertions;
using Grundstuecksfinder.Services.Importers;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers;

public sealed class PostalCodeTests
{
    [Theory]
    [InlineData("14467", "14467")]
    [InlineData(" 01067 ", "01067")]
    [InlineData("2011", null)]              // seen in BB
    [InlineData("Wandlitz", null)]          // seen in BB
    [InlineData("Thälmannstr. 10", null)]   // seen in BB
    [InlineData("66-400", null)]            // Polish, in the OSM areas along the border
    [InlineData("１２３４５", null)]          // non-ASCII digits
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalize_KeepsOnlyFiveDigitCodes(string? value, string? expected) =>
        PostalCode.Normalize(value).Should().Be(expected);
}
