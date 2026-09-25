using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public sealed class ParcelFeatureTypeOptionsTests
{
    [Fact]
    public void Defaults_DescribeInspireCadastralParcels()
    {
        var type = new ParcelFeatureTypeOptions();

        type.TypeName.Should().Be("cp:CadastralParcel");
        type.LocalName.Should().Be("CadastralParcel");
        type.Prefix.Should().Be("cp");
        type.AreaField.Should().Be("areaValue");
        type.Validate().Should().BeEmpty();
    }

    [Fact]
    public void LocalName_WithoutPrefix_IsTheWholeName() =>
        new ParcelFeatureTypeOptions { TypeName = "flurstuecke" }.LocalName.Should().Be("flurstuecke");

    [Fact]
    public void Validate_AlkisVereinfacht_IsValid() =>
        new ParcelFeatureTypeOptions
        {
            TypeName = "ave:Flurstueck",
            Namespace = "http://repository.gdi-de.org/schemas/adv/produkt/alkis-vereinfacht/1.0",
            AreaField = "flaeche",
            GemeindeField = "gemeinde",
            LagebezeichnungField = "lagebeztxt",
        }.Validate().Should().BeEmpty();

    [Fact]
    public void Validate_BrokenValues_AreAllReported()
    {
        var errors = new ParcelFeatureTypeOptions
        {
            TypeName = "ave: Flurstueck",
            Namespace = "not a uri",
            AreaField = " ",
            GemeindeField = "",
            LagebezeichnungField = " ",
        }.Validate().ToList();

        errors.Should().HaveCount(5);
        errors.Should().Contain(e => e.StartsWith("TypeName", StringComparison.Ordinal));
        errors.Should().Contain(e => e.StartsWith("Namespace", StringComparison.Ordinal));
        errors.Should().Contain(e => e.StartsWith("AreaField", StringComparison.Ordinal));
        errors.Should().Contain(e => e.StartsWith("GemeindeField", StringComparison.Ordinal));
        errors.Should().Contain(e => e.StartsWith("LagebezeichnungField", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NamespaceWithoutPrefix_IsRejected() =>
        new ParcelFeatureTypeOptions { TypeName = "Flurstueck", Namespace = "http://example.org/ns" }
            .Validate().Should().ContainSingle().Which.Should().Contain("prefix");
}
