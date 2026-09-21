using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public partial class WfsLiveEndpointTests
{
    // ── Hessen ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task HE_Parcels_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(
            "https://inspire-hessen.de/ows/services/org.2.07247d95-adc7-4c7d-9c7a-ed17af855317_wfs",
            "cp:CadastralParcel",
            "480000,5550000,481000,5551000",
            "urn:ogc:def:crs:EPSG::25832");

        var parcels = WfsGmlParser.ParseCadastralParcels(stream).Features.ToList();
        parcels.Should().NotBeEmpty("HE should have parcels in this tile near Frankfurt");
        parcels.Should().OnlyContain(p => p.AreaM2 > 0);
        parcels.Should().OnlyContain(p => p.Geometry.IsValid);
    }

    [Fact]
    public async Task HE_Addresses_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(
            "https://inspire-hessen.de/ows/services/org.2.19698713-4b13-4938-a9db-96bfdc996451_wfs",
            "ad:Address",
            "480000,5550000,481000,5551000",
            "urn:ogc:def:crs:EPSG::25832",
            resolve: true);

        var addresses = WfsGmlParser.ParseAddresses(stream).Features.ToList();
        addresses.Should().NotBeEmpty("HE should have addresses in this tile near Frankfurt");
        addresses.Should().OnlyContain(a => a.Location != null);
    }
}
