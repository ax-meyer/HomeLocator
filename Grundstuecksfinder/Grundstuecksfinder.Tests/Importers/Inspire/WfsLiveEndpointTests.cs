using Grundstuecksfinder.Infrastructure;
using Grundstuecksfinder.Services.Importers.Inspire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

/// <summary>
/// Smoke tests that hit each configured Bundesland's real endpoints — WFS requests with a tiny bounding
/// box, and the cheap version check of a Hauskoordinaten file (never the file itself) — to verify
/// that the responses can still be parsed. These are live network tests — they will fail
/// if the server is down or changes its response format. Each state's tests live in
/// WfsLiveEndpointTests.&lt;State&gt;.cs next to this file.
/// </summary>
[Trait("Category", "Live")]
public partial class WfsLiveEndpointTests
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// The importer's own gateway, for the checks that go through it (the Hauskoordinaten file
    /// locators): identified like the app, one quick retry, no pacing.
    /// </summary>
    private static InspireServiceClient LiveClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(OutboundHttp.UserAgent);
        return new InspireServiceClient(http, new InspireSourceOptions
        {
            Source = "live",
            MaxAttempts = 2,
            RetryBaseDelaySeconds = 1,
            MaxRetryDelaySeconds = 1,
            MinRequestIntervalSeconds = 0,
        }, NullLogger.Instance, TimeProvider.System);
    }

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
    /// is unusable (see <see cref="Grundstuecksfinder.Services.Importers.Inspire.Addresses.AddressSourceType.InspireWfsStartIndex"/>).
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

    /// <summary>
    /// A page from an OGC API Features "items" endpoint (see
    /// <see cref="Grundstuecksfinder.Services.Importers.Inspire.Addresses.AddressSourceType.OgcApiFeatures"/>),
    /// addressed by limit/offset rather than a WFS bbox.
    /// </summary>
    private static async Task<Stream> FetchOgcApiFeatures(string baseUrl, int limit, int offset = 0)
    {
        var url = $"{baseUrl}?limit={limit}&offset={offset}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Without an explicit Accept, Saarland's server answers its HTML viewer instead of GeoJSON.
        request.Headers.Accept.ParseAdd("application/geo+json");
        var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync();
    }
}
