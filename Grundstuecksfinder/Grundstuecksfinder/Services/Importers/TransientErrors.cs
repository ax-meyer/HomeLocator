using System.Net;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Grundstuecksfinder.Services.Importers;

/// <summary>
/// Which failures of a download are worth another attempt. Shared by the importers so they agree
/// on what "transient" means; each adds the errors only its own format can produce.
/// </summary>
public static class TransientErrors
{
    /// <summary>
    /// Network errors and timeouts are worth retrying, and so are the status codes a server uses
    /// to say "later": 5xx, 408 and 429. Other 4xx mean the request itself is wrong. Only GETs
    /// are ever retried, so a repeat is always safe.
    /// </summary>
    public static bool IsTransient(Exception? ex) => ex switch
    {
        // The breaker's own rejection is not a server failure, and a retry would only re-reject.
        BrokenCircuitException => false,
        HttpRequestException { StatusCode: { } status } => IsTransientStatus(status),
        // No status: the request never got a response (DNS, connect, reset).
        HttpRequestException => true,
        OperationCanceledException or IOException or TimeoutRejectedException => true,
        _ => false,
    };

    public static bool IsTransientStatus(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
}
