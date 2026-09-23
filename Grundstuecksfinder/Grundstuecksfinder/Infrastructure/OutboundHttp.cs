namespace Grundstuecksfinder.Infrastructure;

/// <summary>
/// How this app identifies itself to the public services it downloads from, and the shared
/// registration for those clients.
/// </summary>
public static class OutboundHttp
{
    /// <summary>
    /// Sent on every outbound request. One import is tens of thousands of requests against a
    /// public download service; naming the client and a contact address lets an operator ask us
    /// to slow down or stop instead of only being able to block the address. Nominatim's usage
    /// policy requires it outright, and the state download services are no less entitled to it.
    /// </summary>
    public const string UserAgent = "Grundstuecksfinder/1.0 (+https://grundstuecksfinder.meyerweb.eu; grundstuecksfinder-impressum@meyerweb.eu)";

    /// <summary>
    /// A named client for a bulk download: identified by <see cref="UserAgent"/>, and with no
    /// HttpClient.Timeout because each importer bounds a request itself — HttpClient's own
    /// timeout stops counting once the response headers have arrived, which is no use for the
    /// long streaming bodies these services return.
    /// </summary>
    public static IServiceCollection AddDownloadClient(this IServiceCollection services, string name)
    {
        services.AddHttpClient(name, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        });
        return services;
    }
}
