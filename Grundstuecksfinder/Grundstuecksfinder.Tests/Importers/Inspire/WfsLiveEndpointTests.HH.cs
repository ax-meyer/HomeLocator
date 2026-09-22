using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public partial class WfsLiveEndpointTests
{
    // ── Hamburg ──────────────────────────────────────────────────────────────

    private const string HamburgAddressWfs = "https://geodienste.hamburg.de/HH_WFS_INSPIRE_Adressen";
    private const string Utm32 = "urn:ogc:def:crs:EPSG::25832";

    [Fact]
    public async Task HH_Parcels_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(
            "https://geodienste.hamburg.de/HH_WFS_INSPIRE_Flurstuecke",
            "cp:CadastralParcel",
            "565000,5933000,570000,5938000",
            Utm32);

        var parcels = WfsGmlParser.ParseCadastralParcels(stream).Features.ToList();
        parcels.Should().NotBeEmpty("Hamburg should have parcels in this tile near the centre");
        parcels.Should().OnlyContain(p => p.AreaM2 > 0);
        parcels.Should().OnlyContain(p => p.Geometry.IsValid);
    }

    /// <summary>
    /// Addresses come by startIndex, not bbox: Hamburg's address geometries carry SRID 0, so the
    /// server rejects every bbox filter (see <see cref="HH_Addresses_RejectABboxFilter"/>).
    /// </summary>
    [Fact]
    public async Task HH_Addresses_ParseSuccessfully()
    {
        using var stream = await FetchGetFeaturePage(HamburgAddressWfs, "ad:Address", Utm32, startIndex: 0, resolve: true);

        var addresses = WfsGmlParser.ParseAddresses(stream, isCityState: true, usePostName: false).Features.ToList();
        addresses.Should().NotBeEmpty();
        addresses.Should().OnlyContain(a => a.Location != null);
        addresses.Should().OnlyContain(a => a.Gemeinde == "Hamburg", "the Land itself is the Gemeinde");
        addresses.Should().Contain(a => !string.IsNullOrWhiteSpace(a.Plz));
    }

    /// <summary>
    /// Guards the reason the startIndex option exists: should Hamburg ever fix the SRID of its
    /// address geometries, this test fails and the source can go back to plain tiling.
    /// </summary>
    [Fact]
    public async Task HH_Addresses_RejectABboxFilter()
    {
        var url = $"{HamburgAddressWfs}?service=WFS&version=2.0.0&request=GetFeature" +
                  $"&typenames=ad%3AAddress&bbox=565000,5933000,570000,5938000,{Uri.EscapeDataString(Utm32)}" +
                  $"&srsName={Uri.EscapeDataString(Utm32)}&count=5";

        using var response = await Http.GetAsync(url, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.InternalServerError);
        body.Should().Contain("mixed SRID geometries", "the address points are still stored with SRID 0");
    }

    /// <summary>
    /// Paging is only safe while it is stable, so this checks what the import relies on: a page
    /// is the same across requests, and the page after it doesn't repeat any of it.
    /// </summary>
    [Fact]
    public async Task HH_Addresses_PageStablyByStartIndex()
    {
        var first = await ReadIdsAsync(startIndex: 0);
        var firstAgain = await ReadIdsAsync(startIndex: 0);
        var second = await ReadIdsAsync(startIndex: 50);

        first.Should().HaveCount(50).And.OnlyHaveUniqueItems();
        firstAgain.Should().Equal(first, "the same page must come back in the same order");
        second.Should().NotIntersectWith(first, "adjacent pages must not overlap");
        return;

        async Task<List<string>> ReadIdsAsync(int startIndex)
        {
            using var stream = await FetchGetFeaturePage(
                HamburgAddressWfs, "ad:Address", Utm32, startIndex, count: 50);
            var document = await System.Xml.Linq.XDocument.LoadAsync(
                stream, System.Xml.Linq.LoadOptions.None, TestContext.Current.CancellationToken);
            return document.Descendants()
                .Where(e => e.Name.LocalName == "Address")
                .Select(e => e.Attributes().First(a => a.Name.LocalName == "id").Value)
                .ToList();
        }
    }
}
