using System.Globalization;
using System.Xml.Linq;
using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Grundstuecksfinder.Services.Importers.Inspire.Addresses;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public partial class WfsLiveEndpointTests
{
    // ── Saarland ─────────────────────────────────────────────────────────────
    // The parcel side stays on the INSPIRE cp:CadastralParcel WFS; the address side is the
    // native ALKIS Hauskoordinaten WFS (flat features, PLZ included), whose INSPIRE ad:Address
    // equivalent is ~170x slower for the same data.

    private const string SlHauskoordinatenUrl = "https://geoportal.saarland.de/registry/wfs/416";
    private const string SlHauskoordinatenType = "GDI_ALKIS_Gebaeude:Hauskoordinaten";
    private const string SlTile = "354000,5455400,354500,5455900";
    private const string SlCrs = "urn:ogc:def:crs:EPSG::25832";

    [Fact]
    public async Task SL_Parcels_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(
            "https://geoportal.saarland.de/gdi-sl/inspirewfs_Flurstuecke_Grundstuecke_ALKIS",
            "cp:CadastralParcel", SlTile, SlCrs);

        var parcels = WfsGmlParser.ParseCadastralParcels(stream).Features.ToList();
        parcels.Should().NotBeEmpty("Saarland should have parcels in this tile in Saarbrücken");
        parcels.Should().OnlyContain(p => p.AreaM2 > 0);
        parcels.Should().OnlyContain(p => p.Geometry.IsValid);
    }

    [Fact]
    public async Task SL_Addresses_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(SlHauskoordinatenUrl, SlHauskoordinatenType, SlTile, SlCrs, count: 50);

        var page = WfsGmlParser.ParseFlatAddresses(XDocument.Load(stream), "Hauskoordinaten", new AddressFieldOptions
        {
            Street = "STN", HouseNumber = "HNR", HouseNumberSuffix = "ADZ", Plz = "PLZ", Ort = "ONM", Gemeinde = "ONM",
        });

        page.SrsNames.Should().OnlyContain(name => InspireSourceOptions.ParseEpsgCode(name) == 25832, "the import checks every page's CRS");
        var addresses = page.Features;
        addresses.Should().HaveCount(50, "Saarbrücken's centre has hundreds of addresses in this tile");
        addresses.Should().OnlyContain(a => a.Location!.X > 354000 && a.Location.X < 354500 && a.Location.Y > 5455400 && a.Location.Y < 5455900);
        addresses.Should().OnlyContain(a => !string.IsNullOrEmpty(a.Str) && !string.IsNullOrEmpty(a.Hnr));
        addresses.Should().OnlyContain(a => a.Plz != null && a.Plz.StartsWith("66", StringComparison.Ordinal));
        addresses.Should().OnlyContain(a => a.Gemeinde == "Saarbrücken" && a.Ort == "Saarbrücken");
        addresses.Should().NotContain(a => a.Str!.Contains('�', StringComparison.Ordinal), "umlauts must survive");
    }

    /// <summary>The count the probe and the completeness check rely on.</summary>
    [Fact]
    public async Task SL_Addresses_ReportsNumberMatchedNearTheKnownTotal()
    {
        var url = $"{SlHauskoordinatenUrl}?service=WFS&version=2.0.0&request=GetFeature" +
                  $"&typenames={Uri.EscapeDataString(SlHauskoordinatenType)}&resultType=hits";
        var ct = TestContext.Current.CancellationToken;
        using var response = await Http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var doc = await XDocument.LoadAsync(await response.Content.ReadAsStreamAsync(ct), LoadOptions.None, ct);

        long.Parse(doc.Root!.Attribute("numberMatched")!.Value, CultureInfo.InvariantCulture)
            .Should().BeGreaterThan(300_000, "Saarland has ~335k addresses");
    }
}
