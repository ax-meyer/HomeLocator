using FluentAssertions;
using Grundstuecksfinder.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Grundstuecksfinder.Tests;

/// <summary>
/// The importers' HttpClients identify the app to the public services they download from —
/// without it an operator's only option against tens of thousands of requests is to block us.
/// </summary>
public sealed class DownloadClientRegistrationTests
{
    private static HttpClient Client(string name) =>
        new ServiceCollection().AddDownloadClient(name).BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>().CreateClient(name);

    [Theory]
    [InlineData("Inspire")]
    [InlineData("Nrw")]
    [InlineData("PostcodeAreas")]
    public void DownloadClient_SendsTheContactableUserAgent(string name)
    {
        var userAgent = Client(name).DefaultRequestHeaders.UserAgent.ToString();

        userAgent.Should().StartWith("Grundstuecksfinder/1.0")
            .And.Contain("grundstuecksfinder-impressum@meyerweb.eu", "an operator must be able to reach us");
    }

    [Fact]
    public void DownloadClient_LeavesTimeoutToTheImporter() =>
        // HttpClient.Timeout stops counting once the headers arrive, so each importer bounds the
        // whole request itself; a finite one here would cut long bodies short.
        Client("Inspire").Timeout.Should().Be(Timeout.InfiniteTimeSpan);

    [Fact]
    public void UserAgent_IsAValidHeaderValue()
    {
        // ParseAdd throws on a malformed value, which would take down startup.
        using var client = new HttpClient();
        var act = () => client.DefaultRequestHeaders.UserAgent.ParseAdd(OutboundHttp.UserAgent);

        act.Should().NotThrow();
    }
}
