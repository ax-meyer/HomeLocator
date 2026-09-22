using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public partial class WfsLiveEndpointTests
{
    // ── Schleswig-Holstein ────────────────────────────────────────────────────

    [Fact]
    public async Task SH_Parcels_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(
            "https://service.gdi-sh.de/SH_INSPIREDOWNLOAD_AI_CP_ALKIS",
            "cp:CadastralParcel",
            "540000,6020000,545000,6025000",
            "http://www.opengis.net/def/crs/epsg/0/25832");

        var parcels = WfsGmlParser.ParseCadastralParcels(stream).Features.ToList();
        parcels.Should().NotBeEmpty("SH should have parcels in this tile near Kiel");
        parcels.Should().OnlyContain(p => p.AreaM2 > 0);
        parcels.Should().OnlyContain(p => p.Geometry.IsValid);
    }

    [Fact]
    public async Task SH_Addresses_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(
            "https://service.gdi-sh.de/SH_INSPIREDOWNLOAD_AI_AD",
            "ad:Address",
            "540000,6020000,545000,6025000",
            "http://www.opengis.net/def/crs/epsg/0/25832",
            resolve: true);

        var addresses = WfsGmlParser.ParseAddresses(stream).Features.ToList();
        addresses.Should().NotBeEmpty("SH should have addresses in this tile near Kiel");
        addresses.Should().OnlyContain(a => a.Location != null);
    }
}
