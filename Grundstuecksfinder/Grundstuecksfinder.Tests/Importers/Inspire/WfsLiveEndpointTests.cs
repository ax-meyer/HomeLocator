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
        var url = $"{baseUrl}?service=WFS&version=2.0.0&request=GetFeature" +
                  $"&typenames={Uri.EscapeDataString(typeName)}" +
                  $"&bbox={bbox},{Uri.EscapeDataString(crs)}" +
                  $"&count={count}&startIndex=0{resolveSuffix}";

        var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync();
    }
}
