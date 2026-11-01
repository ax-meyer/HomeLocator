using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public partial class WfsLiveEndpointTests
{
    // ── Rheinland-Pfalz ──────────────────────────────────────────────────────

    /// <summary>
    /// RP has no usable address dataset: its ALKIS-vereinfacht parcels name their own addresses
    /// in lagebeztxt, which is all the import reads (see <see cref="ParcelLagebezeichnungImport"/>).
    /// </summary>
    [Fact]
    public async Task RP_Parcels_CarryAreaGemeindeAndReadableLagebezeichnung()
    {
        using var stream = await FetchGetFeature(
            "https://geo5.service24.rlp.de/wfs/alkis_rp.fcgi",
            "ave:Flurstueck",
            "447000,5539000,447500,5539500",
            "urn:ogc:def:crs:EPSG::25832",
            count: 200);

        var parcels = WfsGmlParser.ParseParcels(stream, new ParcelFeatureTypeOptions
        {
            TypeName = "ave:Flurstueck",
            AreaField = "flaeche",
            GemeindeField = "gemeinde",
            LagebezeichnungField = "lagebeztxt",
        }).Features;

        parcels.Should().NotBeEmpty("Mainz's old town is full of parcels");
        parcels.Should().OnlyContain(p => p.AreaM2 > 0 && p.Geometry.IsValid);
        parcels.Should().OnlyContain(p => p.Gemeinde == "Mainz");
        parcels.SelectMany(p => LagebezeichnungParser.Parse(p.Lagebezeichnung).Addresses)
            .Should().HaveCountGreaterThan(50, "most old-town parcels name a house number");
    }

    /// <summary>RP's buildings name their addresses like its parcels; they pin parcels naming several.</summary>
    [Fact]
    public async Task RP_Buildings_NameReadableAddresses()
    {
        using var stream = await FetchGetFeature(
            "https://geo5.service24.rlp.de/wfs/alkis_rp.fcgi",
            "ave:GebaeudeBauwerk",
            "447000,5539000,447500,5539500",
            "urn:ogc:def:crs:EPSG::25832",
            count: 2000);

        var buildings = WfsGmlParser.ParseBuildings(stream, new BuildingFeatureTypeOptions()).Features;

        buildings.Should().HaveCountGreaterThan(100, "Mainz's old town is full of houses");
        buildings.Should().OnlyContain(b => b.Geometry.IsValid);
        buildings.SelectMany(b => LagebezeichnungParser.Parse(b.Lagebezeichnung).Addresses)
            .Should().HaveCountGreaterThan(100);
    }
}
