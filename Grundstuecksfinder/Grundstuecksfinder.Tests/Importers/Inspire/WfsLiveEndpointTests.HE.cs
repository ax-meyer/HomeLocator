using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Grundstuecksfinder.Services.Importers.Inspire.Addresses;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public partial class WfsLiveEndpointTests
{
    // ── Hessen ───────────────────────────────────────────────────────────────
    // Parcels from the INSPIRE WFS; addresses from the Hauskoordinaten file in HVBG's download
    // center (see AddressSourceType.HkFile). The INSPIRE address WFS replaced every umlaut with
    // U+FFFD; the file's names are intact.

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

    private const string HessenHauskoordinatenListing =
        "https://gds.hessen.de/INTERSHOP/rest/WFS/HLBG-Geodaten-Site/-/downloadcenter?path=Liegenschaftskataster/Hauskoordinaten%20ohne%20Postalische%20Angaben%20(txt)&navigation=all";

    /// <summary>
    /// The listing names the current edition and today's link to it. Only the first bytes of
    /// the file are fetched — enough to show the escaped link works and leads to a ZIP.
    /// </summary>
    [Fact]
    public async Task HE_HauskoordinatenFile_IsListedInTheDownloadCenter()
    {
        var location = await new HessenDownloadCenterLocator(LiveClient(), HessenHauskoordinatenListing)
            .LocateAsync(TestContext.Current.CancellationToken);

        location.Version.Should().MatchRegex(@"^Hauskoordinaten ohne Postalische Angaben-\d{4}-\d{2}\|\d{2}\.\d{2}\.\d{4}$");
        location.Url.Should().MatchRegex(@"^https://gds\.hessen\.de/downloadcenter/\d{8}/.+\.zip$", "the link carries the day it is valid on");

        using var request = new HttpRequestMessage(HttpMethod.Get, location.Url);
        request.Headers.Range = new RangeHeaderValue(0, 3);
        using var response = await Http.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken))
            .Should().Equal((byte)'P', (byte)'K', (byte)3, (byte)4);
    }
}
