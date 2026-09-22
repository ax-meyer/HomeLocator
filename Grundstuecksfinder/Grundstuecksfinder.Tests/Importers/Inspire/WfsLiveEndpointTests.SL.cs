using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public partial class WfsLiveEndpointTests
{
    // ── Saarland ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SL_Parcels_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(
            "https://geoportal.saarland.de/gdi-sl/inspirewfs_Flurstuecke_Grundstuecke_ALKIS",
            "cp:CadastralParcel",
            "354000,5455400,354500,5455900",
            "urn:ogc:def:crs:EPSG::25832");

        var parcels = WfsGmlParser.ParseCadastralParcels(stream).Features.ToList();
        parcels.Should().NotBeEmpty("Saarland should have parcels in this tile in Saarbrücken");
        parcels.Should().OnlyContain(p => p.AreaM2 > 0);
        parcels.Should().OnlyContain(p => p.Geometry.IsValid);
    }

    /// <summary>
    /// Saarland's services advertise only EPSG:4258 and 4326 in their capabilities, but do honour
    /// srsName=EPSG:25832 — which the import depends on, so this asserts the coordinates really
    /// come back as UTM 32 metres rather than degrees.
    /// </summary>
    [Fact]
    public async Task SL_Addresses_ParseSuccessfullyInUtm32()
    {
        using var stream = await FetchGetFeature(
            "https://geoportal.saarland.de/gdi-sl/inspirewfs_Adressen_Hauskoordinaten",
            "ad:Address",
            "354000,5455400,354500,5455900",
            "urn:ogc:def:crs:EPSG::25832",
            resolve: true);

        var addresses = WfsGmlParser.ParseAddresses(stream, isCityState: false, usePostName: false).Features.ToList();
        addresses.Should().NotBeEmpty("Saarland should have addresses in this tile in Saarbrücken");
        addresses.Should().OnlyContain(a => a.Location != null);
        addresses.Should().OnlyContain(a => a.Location!.X > 300000 && a.Location.X < 390000, "coordinates must be UTM 32 metres");
        addresses.Should().Contain(a => !string.IsNullOrWhiteSpace(a.Plz));
        addresses.Should().Contain(a => a.Gemeinde == "Saarbrücken");
    }
}
