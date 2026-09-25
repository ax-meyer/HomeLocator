using FluentAssertions;
using Grundstuecksfinder.Services;
using Xunit;

namespace Grundstuecksfinder.Tests;

public class SearchLocationTests
{
    [Fact]
    public void Combine_ListsPlzBeforeGemeinden()
    {
        var locations = SearchLocation.Combine(["24103", "50667"], ["Kiel", "Köln"]);

        locations.Select(l => (l.Name, l.IsPlz)).Should().Equal(
            ("24103", true), ("50667", true), ("Kiel", false), ("Köln", false));
    }

    [Fact]
    public void Plz_FiltersByPlzOnly()
    {
        var location = new SearchLocation("50667", IsPlz: true);

        (location.Plz, location.Gemeinde).Should().Be(("50667", (string?)null));
    }

    [Fact]
    public void Gemeinde_FiltersByGemeindeOnly()
    {
        var location = new SearchLocation("Köln", IsPlz: false);

        (location.Plz, location.Gemeinde).Should().Be(((string?)null, "Köln"));
    }
}
