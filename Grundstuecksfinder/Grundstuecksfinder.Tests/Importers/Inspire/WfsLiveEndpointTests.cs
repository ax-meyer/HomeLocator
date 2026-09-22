using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

/// <summary>
/// Smoke tests that hit each configured Bundesland's real WFS endpoints with a tiny bounding box to verify
/// that the GML response structure can be parsed. These are live network tests — they will fail
/// if the server is down or changes its response format. Each state's tests live in
/// WfsLiveEndpointTests.&lt;State&gt;.cs next to this file.
/// </summary>
[Trait("Category", "Live")]
public partial class WfsLiveEndpointTests
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static async Task<Stream> FetchGetFeature(string baseUrl, string typeName, string bbox, string crs,
        int count = 5, bool resolve = false)
    {
        var resolveSuffix = resolve ? "&resolve=local&resolvedepth=2" : "";
        // srsName, like the importer sends it: without it a server answers in its own default,
        // which for Saarland is EPSG:4258 — degrees, and in latitude/longitude order.
        var url = $"{baseUrl}?service=WFS&version=2.0.0&request=GetFeature" +
                  $"&typenames={Uri.EscapeDataString(typeName)}" +
                  $"&bbox={bbox},{Uri.EscapeDataString(crs)}" +
                  $"&srsName={Uri.EscapeDataString(crs)}" +
                  $"&count={count}&startIndex=0{resolveSuffix}";

        var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync();
    }

    /// <summary>
    /// A GetFeature page addressed by startIndex rather than bbox, for services whose bbox filter
    /// is unusable (see <see cref="Grundstuecksfinder.Services.Importers.Inspire.InspireSourceOptions.PageAddressesWithStartIndex"/>).
    /// </summary>
    private static async Task<Stream> FetchGetFeaturePage(string baseUrl, string typeName, string crs,
        int startIndex, int count = 5, bool resolve = false)
    {
        var resolveSuffix = resolve ? "&resolve=local&resolvedepth=2" : "";
        var url = $"{baseUrl}?service=WFS&version=2.0.0&request=GetFeature" +
                  $"&typenames={Uri.EscapeDataString(typeName)}" +
                  $"&srsName={Uri.EscapeDataString(crs)}" +
                  $"&count={count}&startIndex={startIndex}{resolveSuffix}";

        var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync();
    }
}
