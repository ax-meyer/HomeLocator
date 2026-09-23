using System.Text.Json;
using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public partial class WfsLiveEndpointTests
{
    // ── Saarland ─────────────────────────────────────────────────────────────
    // The parcel side stays on the INSPIRE cp:CadastralParcel WFS; the address side moves to the
    // native ALKIS Hauskoordinaten OGC API Features endpoint, whose ad:Address WFS equivalent is
    // ~170x slower for the same data (see InspireSourceOptions.UseOgcApiAddresses).

    private const string HauskoordinatenUrl =
        "https://geoportal.saarland.de/spatial-objects/416/collections/GDI_ALKIS_Gebaeude:Hauskoordinaten/items";

    [Fact]
    public async Task SL_Parcels_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(
            "https://geoportal.saarland.de/gdi-sl/inspirewfs_Flurstuecke_Grundstuecke_ALKIS",
            "cp:CadastralParcel",
            "354000,5455400,354500,5455900",
            "urn:ogc:def:crs:EPSG::25832");

        var parcels = WfsGmlParser.ParseCadastralParcels(stream).Features.ToList();
        parcels.Should().NotBeEmpty("Saarland should have parcels in this tile in Saarbrücken");
        parcels.Should().OnlyContain(p => p.AreaM2 > 0);
        parcels.Should().OnlyContain(p => p.Geometry.IsValid);
    }

    /// <summary>
    /// XCOORD/YCOORD carry EPSG:25832 metres with the UTM zone folded into the easting's
    /// ten-millions digit; this asserts <see cref="OgcApiAddressParser"/> strips it back out
    /// correctly rather than leaving coordinates 32 million metres off.
    /// </summary>
    [Fact]
    public async Task SL_Addresses_ParseSuccessfullyFromOgcApi()
    {
        using var stream = await FetchOgcApiFeatures(HauskoordinatenUrl, limit: 50);

        var addresses = OgcApiAddressParser.ParseAddresses(stream).Features.ToList();
        addresses.Should().HaveCount(50);
        addresses.Should().OnlyContain(a => a.Location != null);
        addresses.Should().OnlyContain(a => a.Location!.X > 300000 && a.Location.X < 390000, "coordinates must be UTM 32 metres, not zone-prefixed");
        addresses.Should().OnlyContain(a => a.Location!.Y > 5435000 && a.Location.Y < 5510000);
        addresses.Should().Contain(a => !string.IsNullOrWhiteSpace(a.Plz));
        addresses.Should().Contain(a => !string.IsNullOrWhiteSpace(a.Gemeinde));
        addresses.Should().OnlyContain(a => a.Gemeinde == a.Ort, "this source has no separate Ortsteil level");
    }

    [Fact]
    public async Task SL_Addresses_ReportsNumberMatchedNearTheKnownTotal()
    {
        using var stream = await FetchOgcApiFeatures(HauskoordinatenUrl, limit: 1);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);

        doc.RootElement.GetProperty("numberMatched").GetInt64().Should().BeGreaterThan(300_000, "Saarland has ~335k addresses");
    }

    /// <summary>
    /// Paging is only safe while it is stable, so this checks what the import relies on: a page
    /// is the same across requests, and the page after it doesn't repeat any of it.
    /// </summary>
    [Fact]
    public async Task SL_Addresses_PageStablyByOffset()
    {
        var first = await ReadIdsAsync(offset: 0);
        var firstAgain = await ReadIdsAsync(offset: 0);
        var second = await ReadIdsAsync(offset: 50);

        first.Should().HaveCount(50).And.OnlyHaveUniqueItems();
        firstAgain.Should().Equal(first, "the same page must come back in the same order");
        second.Should().NotIntersectWith(first, "adjacent pages must not overlap");
        return;

        async Task<List<string>> ReadIdsAsync(int offset)
        {
            using var stream = await FetchOgcApiFeatures(HauskoordinatenUrl, limit: 50, offset);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
            return doc.RootElement.GetProperty("features").EnumerateArray()
                .Select(f => f.GetProperty("properties").GetProperty("gml_id").GetString()!)
                .ToList();
        }
    }
}
