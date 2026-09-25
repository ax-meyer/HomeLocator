using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Grundstuecksfinder.Services.Importers.Inspire.Addresses;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public partial class WfsLiveEndpointTests
{
    // ── Baden-Württemberg ────────────────────────────────────────────────────
    // Parcels from the INSPIRE WFS; addresses from LGL's statewide Hauskoordinaten file (see
    // AddressSourceType.HkFile), which replaced hours of address WFS tiles.

    private const string BwHauskoordinatenUrl = "https://opengeodata.lgl-bw.de/data/hk/hk_bw.zip";

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

    /// <summary>
    /// The import only downloads the 73 MB file when its version changed, and the version is
    /// what a HEAD request says. Should LGL stop sending ETag and Last-Modified, BW would never
    /// be imported again — this fails first.
    /// </summary>
    [Fact]
    public async Task BW_HauskoordinatenFile_IsVersionedByItsHeaders()
    {
        var location = await new StaticUrlHkFileLocator(LiveClient(), BwHauskoordinatenUrl)
            .LocateAsync(TestContext.Current.CancellationToken);

        location.Url.Should().Be(BwHauskoordinatenUrl);
        location.Version.Should().MatchRegex("^\".+\"\\|\\d{4}-\\d{2}-\\d{2}T", "both an ETag and a Last-Modified are expected");
    }
}
