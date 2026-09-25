using System.Globalization;
using System.Text.Json;

namespace Grundstuecksfinder.Services.Importers.Inspire.Addresses;

/// <summary>Where a Hauskoordinaten file currently is, and which edition it is.</summary>
/// <param name="Url">The file's download URL, fully escaped.</param>
/// <param name="Version">
/// The publisher's own version marker (ETag, Last-Modified, edition name): it changes exactly
/// when the file does. It is only one part of its source's fingerprint, though: while the
/// parcels' part is an approximate WFS hit count, the whole source is re-imported — the file
/// downloaded again — when that policy says so, even if the file itself is unchanged.
/// </param>
public sealed record HkFileLocation(string Url, string Version);

/// <summary>
/// Finds a source's current Hauskoordinaten file without downloading it — publishers differ in
/// how: a fixed URL whose headers carry the version, or a download portal whose listing does.
/// </summary>
public interface IHkFileLocator
{
    /// <summary>
    /// One attempt, not retried: the caller runs it inside the source's resilience pipeline,
    /// alone for a probe or together with the download it leads to.
    /// </summary>
    Task<HkFileLocation> LocateAsync(CancellationToken ct);
}

/// <summary>
/// A file at a fixed URL (<see cref="HkFileLocatorType.StaticUrl"/>; Baden-Württemberg's
/// opengeodata.lgl-bw.de), versioned by what a HEAD request says about it.
/// </summary>
public sealed class StaticUrlHkFileLocator(InspireServiceClient client, string url) : IHkFileLocator
{
    public async Task<HkFileLocation> LocateAsync(CancellationToken ct)
    {
        using var response = await client.SendAsync(HttpMethod.Head, url, accept: null, ct);
        return new HkFileLocation(url, Version(response.Headers.ETag?.ToString(), response.Content.Headers.LastModified)
            ?? throw new InspireImportException($"{url} carries neither an ETag nor a Last-Modified header, so a new edition can't be told from the last one"));
    }

    /// <summary>
    /// Both markers where the server sends both: either alone changes with a new upload, and
    /// keeping both costs nothing.
    /// </summary>
    internal static string? Version(string? etag, DateTimeOffset? lastModified)
    {
        var modified = lastModified?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return (etag, modified) switch
        {
            (null, null) => null,
            (null, _) => modified,
            (_, null) => etag,
            _ => $"{etag}|{modified}",
        };
    }
}

/// <summary>
/// A file in Hessen's download center (<see cref="HkFileLocatorType.HessenDownloadCenter"/>,
/// gds.hessen.de), found through the center's REST listing of one product folder.
/// </summary>
/// <remarks>
/// The download URL isn't stable: it contains the current date ("/downloadcenter/20260924/…"),
/// works only on that day, answers HEAD with 405, and the file name carries the edition
/// ("…-2026-01.zip"). So the listing is asked every time, and the version is the edition's name
/// and creation date from that listing.
/// </remarks>
public sealed class HessenDownloadCenterLocator(InspireServiceClient client, string listingUrl) : IHkFileLocator
{
    public async Task<HkFileLocation> LocateAsync(CancellationToken ct)
    {
        using var listing = await client.ReadJsonAsync(listingUrl, "application/json", ct);
        return ParseListing(listing, new Uri(listingUrl));
    }

    /// <summary>
    /// The newest ZIP file of a listing: <c>searchresult.downloads[]</c> entries shaped like
    /// <c>{ "name": "Hauskoordinaten ohne Postalische Angaben-2026-01", "fileExtension": "ZIP",
    /// "creationDate": "24.06.2026", "downloadLink": { "uri": "/downloadcenter/20260924/…zip" } }</c>,
    /// the link relative to the listing's host and not escaped (it contains spaces). A link to
    /// any other host is refused.
    /// </summary>
    public static HkFileLocation ParseListing(JsonDocument listing, Uri listingUrl)
    {
        var files = new List<(string Name, string CreationDate, DateTime Created, string Uri)>();
        if (listing.RootElement.TryGetProperty("searchresult", out var result) &&
            result.TryGetProperty("downloads", out var downloads) && downloads.ValueKind == JsonValueKind.Array)
        {
            foreach (var download in downloads.EnumerateArray())
            {
                if (!string.Equals(Text(download, "fileExtension"), "ZIP", StringComparison.OrdinalIgnoreCase)) continue;
                var name = Text(download, "name");
                var created = Text(download, "creationDate");
                var uri = download.TryGetProperty("downloadLink", out var link) ? Text(link, "uri") : null;
                if (name is null || created is null || uri is null) continue;

                // An unreadable date still makes a usable version; it only loses the tiebreak.
                var date = DateTime.TryParseExact(created, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    ? d : DateTime.MinValue;
                files.Add((name, created, date, uri));
            }
        }

        if (files.Count == 0)
            throw new InspireImportException($"the download center lists no ZIP file at {listingUrl}");

        var newest = files.OrderByDescending(f => f.Created).ThenByDescending(f => f.Name, StringComparer.Ordinal).First();
        var url = new Uri(listingUrl, EscapePath(newest.Uri));
        // The link is meant to be relative to the listing's host; one naming another host
        // ("//elsewhere/…") is refused rather than downloaded from there.
        if (Uri.Compare(url, listingUrl, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
            throw new InspireImportException($"the download center links {newest.Name} to {url}, not to its own host {listingUrl.Host}");
        return new HkFileLocation(url.AbsoluteUri, $"{newest.Name}|{newest.CreationDate}");
    }

    /// <summary>Escapes each path segment; unescaping first keeps an already escaped link intact.</summary>
    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/').Select(segment => Uri.EscapeDataString(Uri.UnescapeDataString(segment))));

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 } text ? text.Trim() : null
            : null;
}
