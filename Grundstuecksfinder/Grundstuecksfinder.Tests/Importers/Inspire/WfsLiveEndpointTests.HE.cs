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

    // ── Hessen's name catalogue (the sources that still carry intact spellings) ──

    /// <summary>
    /// Hessen's address export replaces every non-ASCII character with U+FFFD. These two
    /// services, from the same authority, still carry the spellings, and their keys line up
    /// with the address service's component ids. If either ever changes, the repair silently
    /// stops working, so both are pinned here.
    /// </summary>
    [Fact]
    public async Task HE_AdministrativeUnits_CarryIntactNamesKeyedByAgs()
    {
        var url = "https://inspire-hessen.de/ows/services/org.2.c049a647-8e7e-4ef2-9e6f-c7f53ada57dc_wfs" +
                  "?service=WFS&version=2.0.0&request=GetFeature&typenames=au%3AAdministrativeUnit&count=400";

        var body = await Http.GetStringAsync(url, TestContext.Current.CancellationToken);

        body.Should().NotContain("\uFFFD", "this is the dataset the damaged names are repaired from");
        body.Should().Contain("<au:nationalCode>", "the AGS is what AdminUnitName ids are keyed by");
        body.Should().Contain("ü", "German names must arrive intact");
    }

    [Fact]
    public async Task HE_StreetCatalogue_CarriesIntactNamesKeyedByAgsAndStreetKey()
    {
        // Fuldabrück's "Elisabethenstraße": AdminUnitName_06435003 + street key 233, which the
        // address service publishes as ThoroughfareName_0643500300233.
        var filter = Uri.EscapeDataString(
            """<fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0" xmlns:adv="http://www.adv-online.de/namespaces/adv/gid/7.1"><fes:PropertyIsEqualTo><fes:ValueReference>adv:schluesselGesamt</fes:ValueReference><fes:Literal>06435003233</fes:Literal></fes:PropertyIsEqualTo></fes:Filter>""");
        var url = "https://www.gds.hessen.de/wfs2/aaa-suite/cgi-bin/alkis/sf/wfs" +
                  "?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature" +
                  "&TYPENAMES=adv%3AAX_LagebezeichnungKatalogeintrag&FILTER=" + filter;

        var body = await Http.GetStringAsync(url, TestContext.Current.CancellationToken);

        body.Should().Contain("<bezeichnung>Elisabethenstraße</bezeichnung>",
            "the catalogue is what turns the address service's \"Elisabethenstra\uFFFDe\" back into a name");
    }
}
