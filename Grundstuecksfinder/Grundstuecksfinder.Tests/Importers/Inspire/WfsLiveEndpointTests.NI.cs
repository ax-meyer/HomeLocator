using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public partial class WfsLiveEndpointTests
{
    // ── Niedersachsen ────────────────────────────────────────────────────────

    [Fact]
    public async Task NI_Parcels_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(
            "https://www.inspire.niedersachsen.de/doorman/noauth/alkis-dls-cp",
            "cp:CadastralParcel",
            "547500,5800500,552500,5805500",
            "urn:ogc:def:crs:EPSG::25832");

        var parcels = WfsGmlParser.ParseCadastralParcels(stream).Features.ToList();
        parcels.Should().NotBeEmpty("NI should have parcels in this tile near Hannover");
        parcels.Should().OnlyContain(p => p.AreaM2 > 0);
        parcels.Should().OnlyContain(p => p.Geometry.IsValid);
    }

    /// <summary>
    /// NI writes its address elements in the default namespace (&lt;Address&gt;, not &lt;ad:Address&gt;)
    /// and answers in the "http://.../epsg/0/25832" srsName form, so this also guards the
    /// parser's local-name matching and <see cref="InspireSourceOptions.ParseEpsgCode"/>.
    /// </summary>
    [Fact]
    public async Task NI_Addresses_ParseSuccessfully()
    {
        using var stream = await FetchGetFeature(
            "https://www.inspire.niedersachsen.de/doorman/noauth/alkis-dls-ad",
            "ad:Address",
            "547500,5800500,552500,5805500",
            "urn:ogc:def:crs:EPSG::25832",
            resolve: true);

        var addresses = WfsGmlParser.ParseAddresses(stream).Features.ToList();
        addresses.Should().NotBeEmpty("NI should have addresses in this tile near Hannover");
        addresses.Should().OnlyContain(a => a.Location != null);
        addresses.Should().Contain(a => !string.IsNullOrWhiteSpace(a.Str), "streets come from ThoroughfareName");
        addresses.Should().OnlyContain(a => a.Plz == null, "NI publishes no PostalDescriptor at all");
    }
}
