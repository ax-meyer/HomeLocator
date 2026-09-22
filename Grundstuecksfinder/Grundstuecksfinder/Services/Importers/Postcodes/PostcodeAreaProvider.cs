using System.IO.Compression;
using NetTopologySuite.Geometries;

namespace Grundstuecksfinder.Services.Importers.Postcodes;

/// <summary>Loads postcode areas for one source's extent.</summary>
public interface IPostcodeAreaProvider
{
    /// <param name="utmEpsg">The source's CRS, ETRS89/UTM (EPSG 25831–25833).</param>
    /// <param name="bbox">The source's extent in that CRS; only areas touching it are loaded.</param>
    /// <param name="ct">Cancels the download; parsing, once started, runs to the end.</param>
    Task<IPostcodeLookup> LoadAsync(int utmEpsg, Envelope bbox, CancellationToken ct);
}

/// <summary>"Import:PostcodeAreas" config.</summary>
public sealed class PostcodeAreaOptions
{
    /// <summary>
    /// GeoJSON of all German postcode areas, Brotli-compressed if the URL ends in ".br". The
    /// default is OpenStreetMap's postal_code boundaries as published by yetzt/postleitzahlen
    /// (ODbL; attributed on the Impressum page), updated a few times a year.
    /// </summary>
    public string Url { get; set; } = "https://github.com/yetzt/postleitzahlen/releases/latest/download/postleitzahlen.geojson.br";

    /// <summary>Limit for downloading the file, body included.</summary>
    public double DownloadTimeoutSeconds { get; set; } = 600;
}

/// <summary>
/// Downloads the postcode areas afresh for every import that needs them: imports run rarely,
/// and the file (~25 MB compressed) is too large to be worth keeping in memory between them.
/// </summary>
public sealed partial class PostcodeAreaProvider(
    IHttpClientFactory httpClientFactory,
    PostcodeAreaOptions options,
    ILogger<PostcodeAreaProvider> logger) : IPostcodeAreaProvider
{
    public const string HttpClientName = "PostcodeAreas";

    public async Task<IPostcodeLookup> LoadAsync(int utmEpsg, Envelope bbox, CancellationToken ct)
    {
        // Buffered (it's small compressed) so the timeout also covers a download stalling
        // mid-body, which HttpClient.Timeout doesn't once the headers are in.
        using var body = new MemoryStream();
        using (var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            downloadCts.CancelAfter(TimeSpan.FromSeconds(options.DownloadTimeoutSeconds));
            var http = httpClientFactory.CreateClient(HttpClientName);
            using var response = await http.GetAsync(options.Url, HttpCompletionOption.ResponseHeadersRead, downloadCts.Token);
            response.EnsureSuccessStatusCode();
            await response.Content.CopyToAsync(body, downloadCts.Token);
        }
        body.Position = 0;

        await using Stream geoJson = options.Url.EndsWith(".br", StringComparison.OrdinalIgnoreCase)
            ? new BrotliStream(body, CompressionMode.Decompress, leaveOpen: true)
            : body;

        // Parsing is synchronous (streamed JSON); keep it off the caller's thread.
        var index = await Task.Run(() => PostcodeAreaIndex.Load(geoJson, utmEpsg, bbox), ct);
        LogLoaded(logger, index.Count, options.Url);
        return index;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Count} postcode areas from {Url}")]
    private static partial void LogLoaded(ILogger logger, int count, string url);
}
