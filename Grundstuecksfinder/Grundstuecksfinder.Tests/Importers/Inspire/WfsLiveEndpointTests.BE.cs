using System.Xml.Linq;
using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Grundstuecksfinder.Services.Importers.Inspire.Addresses;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public partial class WfsLiveEndpointTests
{
    // ── Berlin ───────────────────────────────────────────────────────────────

    private const string BeTile = "389500,5818500,390500,5819500";

    [Fact]
    public async Task BE_Parcels_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(
            "https://gdi.berlin.de/services/wfs/alkis_flurstuecke",
            "alkis_flurstuecke:flurstuecke", BeTile, "urn:ogc:def:crs:EPSG::25833", count: 50);

        var parcels = WfsGmlParser.ParseParcels(stream, new ParcelFeatureTypeOptions
        {
            TypeName = "alkis_flurstuecke:flurstuecke", AreaField = "afl",
        }).Features;

        parcels.Should().NotBeEmpty("Mitte is full of parcels");
        parcels.Should().OnlyContain(p => p.AreaM2 > 0 && p.Geometry.IsValid);
    }

    /// <summary>Berlin's address register: flat features with PLZ and Ortsteil, but no Gemeinde.</summary>
    [Fact]
    public async Task BE_Addresses_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(
            "https://gdi.berlin.de/services/wfs/adressen_berlin",
            "adressen_berlin:adressen_berlin", BeTile, "urn:ogc:def:crs:EPSG::25833", count: 50);

        var addresses = WfsGmlParser.ParseFlatAddresses(XDocument.Load(stream), "adressen_berlin", new AddressFieldOptions
        {
            Street = "str_name", HouseNumber = "hnr", HouseNumberSuffix = "hnr_zusatz", Plz = "plz", Ort = "ort_name", FixedGemeinde = "Berlin",
        }).Features;

        addresses.Should().NotBeEmpty();
        addresses.Should().OnlyContain(a => !string.IsNullOrEmpty(a.Str) && !string.IsNullOrEmpty(a.Hnr));
        addresses.Should().OnlyContain(a => a.Plz != null && a.Plz.StartsWith("10", StringComparison.Ordinal));
        addresses.Should().OnlyContain(a => a.Gemeinde == "Berlin" && a.Ort != null && a.Ort != "Berlin");
    }
}
