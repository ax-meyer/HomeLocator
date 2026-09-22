using FluentAssertions;
using Grundstuecksfinder.Services;
using Xunit;

namespace Grundstuecksfinder.Tests;

public class SupportedStatesTests
{
    [Fact]
    public void FromSources_MapsSlugsToSortedNames() =>
        SupportedStates.FromSources(["sh", "nrw", "sn"]).Names
            .Should().Equal("Nordrhein-Westfalen", "Sachsen", "Schleswig-Holstein");

    [Fact]
    public void FromSources_IgnoresCaseAndDuplicates() =>
        SupportedStates.FromSources(["BB", "bb"]).Names.Should().Equal("Brandenburg");

    [Fact]
    public void FromSources_KeepsUnknownSlug() =>
        SupportedStates.FromSources(["xx"]).Names.Should().Equal("xx");

    [Fact]
    public void FromSources_Empty() =>
        SupportedStates.FromSources([]).Names.Should().BeEmpty();
}
