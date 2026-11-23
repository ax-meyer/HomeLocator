namespace Grundstuecksfinder.Infrastructure;

/// <summary>
/// <c>dotnet Grundstuecksfinder.dll --healthcheck</c>: asks the running app's <c>/health</c>
/// endpoint and returns the exit code for the container health check, 0 for a 2xx answer and
/// 1 for anything else. The image has no shell and no curl, so docker-compose's health check
/// runs the app itself in this mode. It returns before the web host is built, so a probe costs
/// one runtime start and one request, not a second app.
/// </summary>
public static class HealthProbe
{
    public const string Argument = "--healthcheck";

    /// <summary>The app's own endpoint: the image serves on port 8080 (ASPNETCORE_HTTP_PORTS).</summary>
    public static readonly Uri DefaultUri = new("http://localhost:8080/health");

    /// <summary>Below docker-compose's 10 s health-check timeout, so the probe reports the failure itself.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(Uri uri, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = timeout ?? DefaultTimeout;
        try
        {
            using var response = await client.GetAsync(uri);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        // Not listening yet, connection refused, or no answer within the timeout.
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }
}
