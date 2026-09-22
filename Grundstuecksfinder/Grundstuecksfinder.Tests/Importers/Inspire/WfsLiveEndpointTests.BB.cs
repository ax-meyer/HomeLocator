using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public partial class WfsLiveEndpointTests
{
    // ── Brandenburg ──────────────────────────────────────────────────────────

    [Fact]
    public async Task BB_Parcels_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(
            "https://inspire.brandenburg.de/services/cp_alkis_wfs",
            "cp:CadastralParcel",
            "390000,5800000,391000,5801000",
            "urn:ogc:def:crs:EPSG::25833");

        var parcels = WfsGmlParser.ParseCadastralParcels(stream).Features.ToList();
        parcels.Should().NotBeEmpty("BB should have parcels in this tile near Potsdam");
        parcels.Should().OnlyContain(p => p.AreaM2 > 0);
        parcels.Should().OnlyContain(p => p.Geometry.IsValid);
    }

    [Fact]
    public async Task BB_Addresses_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(
            "https://inspire.brandenburg.de/services/ad_alkis_wfs",
            "ad:Address",
            "390000,5800000,391000,5801000",
            "urn:ogc:def:crs:EPSG::25833",
            resolve: true);

        var addresses = WfsGmlParser.ParseAddresses(stream).Features.ToList();
        addresses.Should().NotBeEmpty("BB should have addresses in this tile near Potsdam");
        addresses.Should().OnlyContain(a => a.Location != null);
    }
}
