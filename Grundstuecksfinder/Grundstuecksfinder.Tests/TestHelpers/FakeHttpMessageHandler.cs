using System.Net;

namespace Grundstuecksfinder.Tests.TestHelpers;

/// <summary>
/// HTTP message handler that returns pre-configured responses keyed by request URL.
/// Falls back to a default response for any unregistered URL.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new();
    private Func<HttpResponseMessage>? _default;

    public void AddRoute(string url, Func<HttpResponseMessage> factory) =>
        _routes[url] = factory;

    public void SetDefault(Func<HttpResponseMessage> factory) =>
        _default = factory;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var factory = _routes.GetValueOrDefault(url) ?? _default;
        var response = factory?.Invoke()
            ?? new HttpResponseMessage(HttpStatusCode.NotFound) { ReasonPhrase = $"No fake for {url}" };
        return Task.FromResult(response);
    }
}
