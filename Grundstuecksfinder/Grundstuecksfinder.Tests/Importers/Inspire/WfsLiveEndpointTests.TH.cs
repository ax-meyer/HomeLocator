using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public partial class WfsLiveEndpointTests
{
    // ── Thüringen ────────────────────────────────────────────────────────────

    private const string ThAveNamespace = "http://repository.gdi-de.org/schemas/adv/produkt/alkis-vereinfacht/1.0";

    /// <summary>
    /// TH's ALKIS-vereinfacht parcels name their own addresses like RP's; its server only finds
    /// the feature type with the ave prefix bound via NAMESPACES.
    /// </summary>
    [Fact]
    public async Task TH_Parcels_CarryAreaGemeindeAndReadableLagebezeichnung()
    {
        using var stream = await FetchGetFeature(
            "https://www.geoproxy.geoportal-th.de/geoproxy/services/adv_alkis_wfs",
            "ave:Flurstueck",
            "642000,5649000,642500,5649500",
            "urn:ogc:def:crs:EPSG::25832",
            count: 200,
            typeNamespace: ThAveNamespace);

        var parcels = WfsGmlParser.ParseParcels(stream, new ParcelFeatureTypeOptions
        {
            TypeName = "ave:Flurstueck",
            Namespace = ThAveNamespace,
            AreaField = "flaeche",
            GemeindeField = "gemeinde",
            LagebezeichnungField = "lagebeztxt",
        }).Features;

        parcels.Should().NotBeEmpty("Erfurt's centre is full of parcels");
        parcels.Should().OnlyContain(p => p.AreaM2 > 0 && p.Geometry.IsValid);
        parcels.Should().OnlyContain(p => p.Gemeinde == "Erfurt");
        parcels.SelectMany(p => LagebezeichnungParser.Parse(p.Lagebezeichnung).Addresses)
            .Should().HaveCountGreaterThan(50, "most parcels in the centre name a house number");
    }
}
