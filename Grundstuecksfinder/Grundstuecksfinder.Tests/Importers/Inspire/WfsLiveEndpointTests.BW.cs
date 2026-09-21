using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public partial class WfsLiveEndpointTests
{
    // ── Baden-Württemberg ────────────────────────────────────────────────────

    [Fact]
    public async Task BW_Parcels_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(
            "https://owsproxy.lgl-bw.de/owsproxy/wfs/WFS_INSP_BW_Flst_ALKIS",
            "cp:CadastralParcel",
            "470000,5350000,475000,5355000",
            "urn:ogc:def:crs:EPSG::25832");

        var parcels = WfsGmlParser.ParseCadastralParcels(stream).Features.ToList();
        parcels.Should().NotBeEmpty("BW should have parcels in this tile near Freiburg");
        parcels.Should().OnlyContain(p => p.AreaM2 > 0);
        parcels.Should().OnlyContain(p => p.Geometry.IsValid);
    }

    [Fact]
    public async Task BW_Addresses_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(
            "https://owsproxy.lgl-bw.de/owsproxy/wfs/WFS_INSP_BW_Adr_Hauskoord_ALKIS",
            "ad:Address",
            "470000,5350000,475000,5355000",
            "urn:ogc:def:crs:EPSG::25832",
            resolve: true);

        var addresses = WfsGmlParser.ParseAddresses(stream).Features.ToList();
        addresses.Should().NotBeEmpty("BW should have addresses in this tile near Freiburg");
        addresses.Should().OnlyContain(a => a.Location != null);
    }
}
