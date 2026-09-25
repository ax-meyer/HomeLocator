using System.Net;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// The upstream data is incomplete or inconsistent (wrong CRS, too few addresses, a tile that
/// can't be fetched completely, a service without a feature count). Not retried; the probe or
/// import fails and the previous data stays.
/// </summary>
public sealed class InspireImportException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>A request answered with a non-success status, and the server's Retry-After if any.</summary>
public sealed class SourceHttpException(HttpStatusCode statusCode, TimeSpan? retryAfter, string url)
    : HttpRequestException($"{(int)statusCode} {statusCode} from {url}", null, statusCode)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>A WFS answered with something other than a complete FeatureCollection. Retried.</summary>
public sealed class WfsResponseException(string message) : Exception(message);
