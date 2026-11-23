using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Grundstuecksfinder.Infrastructure;
using Grundstuecksfinder.Tests.TestHelpers;
using Xunit;

namespace Grundstuecksfinder.Tests;

public sealed class HealthProbeTests
{
    private static readonly Uri Health = new("http://localhost:8080/health");

    [Fact]
    public async Task Healthy_answer_exits_with_0()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(Health.ToString(), () => new HttpResponseMessage(HttpStatusCode.OK));

        (await HealthProbe.RunAsync(Health, handler)).Should().Be(0);
        handler.RequestedUris.Should().Equal(Health);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)] // a health check reports Unhealthy
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Error_answer_exits_with_1(HttpStatusCode status)
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetDefault(() => new HttpResponseMessage(status));

        (await HealthProbe.RunAsync(Health, handler)).Should().Be(1);
    }

    [Fact]
    public async Task Unreachable_app_exits_with_1()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetDefault(() => throw new HttpRequestException("Connection refused",
            new SocketException((int)SocketError.ConnectionRefused)));

        (await HealthProbe.RunAsync(Health, handler)).Should().Be(1);
    }

    [Fact]
    public async Task No_answer_within_the_timeout_exits_with_1()
    {
        using var handler = new HangingHandler();

        (await HealthProbe.RunAsync(Health, handler, TimeSpan.FromMilliseconds(50))).Should().Be(1);
    }

    [Fact]
    public async Task Nothing_listening_on_the_port_exits_with_1()
    {
        // A real request to a port nobody listens on: the connection is refused.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        (await HealthProbe.RunAsync(new Uri($"http://127.0.0.1:{port}/health"))).Should().Be(1);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
