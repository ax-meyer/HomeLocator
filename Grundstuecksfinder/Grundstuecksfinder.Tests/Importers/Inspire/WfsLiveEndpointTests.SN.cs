using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public partial class WfsLiveEndpointTests
{
    // ── Sachsen ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SN_Parcels_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(
            "https://geodienste.sachsen.de/aaa/public_inspire/alkis/cp/dls/wfs",
            "cp:CadastralParcel",
            "400000,5650000,401000,5651000",
            "urn:ogc:def:crs:EPSG::25833");

        var parcels = WfsGmlParser.ParseCadastralParcels(stream).Features.ToList();
        parcels.Should().NotBeEmpty("SN should have parcels in this tile near Leipzig");
        parcels.Should().OnlyContain(p => p.AreaM2 > 0);
        parcels.Should().OnlyContain(p => p.Geometry.IsValid);
    }

    [Fact]
    public async Task SN_Addresses_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(
            "https://geodienste.sachsen.de/aaa/public_inspire/alkis/ad/dls/wfs",
            "ad:Address",
            "400000,5650000,401000,5651000",
            "urn:ogc:def:crs:EPSG::25833",
            resolve: true);

        var addresses = WfsGmlParser.ParseAddresses(stream).Features.ToList();
        addresses.Should().NotBeEmpty("SN should have addresses in this tile near Leipzig");
        addresses.Should().OnlyContain(a => a.Location != null);
    }
}
