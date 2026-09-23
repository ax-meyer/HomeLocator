using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

/// <summary>
/// The rule that decides when a catalogue spelling may replace what a source published.
/// </summary>
public class NameCatalogTests
{
    private const char Lost = NameCatalog.Lost;

    private static NameCatalog Catalog(params (string Id, string Name)[] entries) =>
        new(entries.ToDictionary(e => e.Id, e => e.Name, StringComparer.Ordinal));

    [Fact]
    public void Repair_DamagedName_UsesTheCatalogueSpelling()
    {
        var catalog = Catalog(("ThoroughfareName_0643500300233", "Elisabethenstraße"));

        catalog.Repair("ThoroughfareName_0643500300233", $"Elisabethenstra{Lost}e")
            .Should().Be("Elisabethenstraße");
        catalog.Repaired.Should().Be(1);
    }

    [Fact]
    public void Repair_SeveralLostCharacters_AreAllRestored()
    {
        var catalog = Catalog(("AdminUnitName_06631011", "Großenlüder"));

        catalog.Repair("AdminUnitName_06631011", $"Gro{Lost}enl{Lost}der").Should().Be("Großenlüder");
    }

    [Fact]
    public void Repair_UndamagedName_IsLeftAlone()
    {
        var catalog = Catalog(("ThoroughfareName_1", "Hauptstraße"));

        catalog.Repair("ThoroughfareName_1", "Bahnhofsweg").Should().BeNull("nothing was lost");
        catalog.Repaired.Should().Be(0);
        catalog.Unrepairable.Should().Be(0);
    }

    /// <summary>
    /// The catalogue and the address export are separate snapshots, so a key is occasionally
    /// reused for a different street. Replacing the name then would be worse than keeping it.
    /// </summary>
    [Fact]
    public void Repair_CatalogueNameOfADifferentStreet_IsRejected()
    {
        var catalog = Catalog(("ThoroughfareName_0663300700144", "Zum Mönchsgut"));

        catalog.Repair("ThoroughfareName_0663300700144", $"Hermann-Gmeiner-Stra{Lost}e").Should().BeNull();
        catalog.Repaired.Should().Be(0);
        catalog.Unrepairable.Should().Be(1);
    }

    [Fact]
    public void Repair_UnknownId_IsCountedAsUnrepairable()
    {
        var catalog = Catalog(("ThoroughfareName_1", "Hauptstraße"));

        catalog.Repair("ThoroughfareName_2", $"M{Lost}hlweg").Should().BeNull();
        catalog.Unrepairable.Should().Be(1);
    }

    [Theory]
    [InlineData("Straße", "Stra�e")]
    [InlineData("Großenlüder", "Gro�enl�der")]
    [InlineData("Äbbelallee", "�bbelallee")]
    [InlineData("Kekuléstraße", "Kekul�stra�e")]
    [InlineData("Bahnhofsweg", "Bahnhofsweg")]
    public void Damage_ReplacesEveryNonAsciiCharacter(string intact, string expected) =>
        NameCatalog.Damage(intact).Should().Be(expected);
}
