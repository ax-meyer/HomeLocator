using System.Xml.Linq;
using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Grundstuecksfinder.Services.Importers.Inspire.Addresses;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public partial class WfsLiveEndpointTests
{
    // ── Bremen ───────────────────────────────────────────────────────────────

    private const string HbAppNamespace = "http://www.deegree.org/app";
    private const string HbTile = "486500,5880500,487000,5881000";

    [Fact]
    public async Task HB_Parcels_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(
            "https://geodienste.bremen.de/wfs_hduk2958loah3976niun",
            "app:flurstuecke", HbTile, "urn:ogc:def:crs:EPSG::25832", count: 50, typeNamespace: HbAppNamespace);

        var parcels = WfsGmlParser.ParseParcels(stream, new ParcelFeatureTypeOptions
        {
            TypeName = "app:flurstuecke", Namespace = HbAppNamespace, AreaField = "flaeche",
        }).Features;

        parcels.Should().NotBeEmpty("Bremen's old town is full of parcels");
        parcels.Should().OnlyContain(p => p.AreaM2 > 0 && p.Geometry.IsValid);
    }

    /// <summary>Bremen's native ALKIS Gebäudeadressen: flat features, every one with its PLZ.</summary>
    [Fact]
    public async Task HB_Addresses_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(
            "https://geodienste.bremen.de/wfs_AX_GeoreferenzierteGebaeudeadresse",
            "app:adressen", HbTile, "urn:ogc:def:crs:EPSG::25832", count: 50, typeNamespace: HbAppNamespace);

        var addresses = WfsGmlParser.ParseFlatAddresses(XDocument.Load(stream), "adressen", new AddressFieldOptions
        {
            Street = "stn", HouseNumber = "hnr", HouseNumberSuffix = "adz", Plz = "plz", Ort = "onm", Gemeinde = "onm",
        }).Features;

        addresses.Should().NotBeEmpty();
        addresses.Should().OnlyContain(a => !string.IsNullOrEmpty(a.Str) && !string.IsNullOrEmpty(a.Hnr));
        addresses.Should().OnlyContain(a => a.Plz != null && a.Plz.StartsWith("28", StringComparison.Ordinal));
        addresses.Should().OnlyContain(a => a.Gemeinde == "Bremen" && a.Ort == "Bremen");
    }
}
