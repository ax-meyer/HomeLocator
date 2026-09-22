using System.IO.Compression;
using System.Net;
using System.Text;
using FakeItEasy;
using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Postcodes;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Postcodes;

public sealed class PostcodeAreaProviderTests
{
    private const string GeoJson = """
        { "type": "FeatureCollection", "features": [
          { "type": "Feature", "properties": { "postcode": "14467" },
            "geometry": { "type": "Polygon", "coordinates": [[[13.0, 52.35], [13.1, 52.35], [13.1, 52.45], [13.0, 52.45], [13.0, 52.35]]] } } ] }
        """;

    private static readonly Point Potsdam = new(367000, 5807000);
    private static readonly Envelope PotsdamBbox = new(360000, 375000, 5800000, 5815000);

    private static PostcodeAreaProvider Provider(string url, Func<HttpResponseMessage> response)
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(url, response);
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(PostcodeAreaProvider.HttpClientName)).ReturnsLazily(() => new HttpClient(handler));
        return new PostcodeAreaProvider(factory, new PostcodeAreaOptions { Url = url }, NullLogger<PostcodeAreaProvider>.Instance);
    }

    private static byte[] Brotli(string text)
    {
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.Fastest))
            brotli.Write(Encoding.UTF8.GetBytes(text));
        return compressed.ToArray();
    }

    [Fact]
    public async Task LoadAsync_BrotliUrl_DecompressesTheFile()
    {
        var provider = Provider("http://fake/plz.geojson.br",
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Brotli(GeoJson)) });

        var lookup = await provider.LoadAsync(25833, PotsdamBbox, TestContext.Current.CancellationToken);

        lookup.FindPostcode(Potsdam).Should().Be("14467");
    }

    [Fact]
    public async Task LoadAsync_PlainUrl_ReadsTheFileAsIs()
    {
        var provider = Provider("http://fake/plz.geojson",
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(GeoJson) });

        var lookup = await provider.LoadAsync(25833, PotsdamBbox, TestContext.Current.CancellationToken);

        lookup.FindPostcode(Potsdam).Should().Be("14467");
    }

    [Fact]
    public async Task LoadAsync_DownloadFails_Throws()
    {
        var provider = Provider("http://fake/plz.geojson.br", () => new HttpResponseMessage(HttpStatusCode.NotFound));

        var act = () => provider.LoadAsync(25833, PotsdamBbox, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
