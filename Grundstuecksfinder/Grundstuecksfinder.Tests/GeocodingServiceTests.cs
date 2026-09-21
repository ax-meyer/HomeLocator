using System.Net;
using System.Text;
using FakeItEasy;
using Xunit;
using FluentAssertions;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.Extensions.Caching.Memory;

namespace Grundstuecksfinder.Tests;

public sealed class GeocodingServiceTests : IDisposable
{
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly MemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly GeocodingService _service;

    public GeocodingServiceTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("https://nominatim.openstreetmap.org/") };
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient("Nominatim")).Returns(http);
        _service = new GeocodingService(factory, _cache);
    }

    public void Dispose() => _cache.Dispose();

    private static Property SampleProperty(string str = "Hauptstraße", string hnr = "1",
        string plz = "50667", string ort = "Köln") =>
        new() { Str = str, Hnr = hnr, Plz = plz, Ort = ort, Gemeinde = ort };

    [Fact]
    public async Task GeocodeAsync_ValidAddress_ReturnsCoordinates()
    {
        _handler.SetDefault(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """[{"lat":"50.938361","lon":"6.959974"}]""", Encoding.UTF8, "application/json")
        });

        var result = await _service.GeocodeAsync(SampleProperty());

        result.Should().NotBeNull();
        result!.Value.Lat.Should().BeApproximately(50.938361, 0.0001);
        result.Value.Lon.Should().BeApproximately(6.959974, 0.0001);
    }

    [Fact]
    public async Task GeocodeAsync_EmptyResult_ReturnsNull()
    {
        _handler.SetDefault(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        });

        var result = await _service.GeocodeAsync(SampleProperty());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GeocodeAsync_HttpFailure_ReturnsNull()
    {
        _handler.SetDefault(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await _service.GeocodeAsync(SampleProperty());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GeocodeAsync_SameAddress_ReturnsCachedResult()
    {
        var callCount = 0;
        _handler.SetDefault(() =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"lat":"51.514244","lon":"7.468429"}]""", Encoding.UTF8, "application/json")
            };
        });

        var property = SampleProperty(str: "Bergstraße", plz: "44139", ort: "Dortmund");
        var first = await _service.GeocodeAsync(property);
        var second = await _service.GeocodeAsync(property);

        callCount.Should().Be(1, "second call should be served from cache");
        second.Should().Be(first);
    }

    [Fact]
    public async Task GeocodeAsync_PropertyWithNoAddressInfo_ReturnsNull()
    {
        var empty = new Property { FlaecheAmtl = 200 };

        var result = await _service.GeocodeAsync(empty);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("Stadt Pirna", "Pirna")]
    [InlineData("Kiel, Landeshauptstadt", "Kiel")]
    [InlineData("Cottbus [Chóśebuz]", "Cottbus")]
    public async Task GeocodeAsync_OfficialPlaceName_QueriesNominatimWithPlainName(string ort, string expectedCity)
    {
        _handler.SetDefault(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        });

        await _service.GeocodeAsync(SampleProperty(ort: ort));

        Uri.UnescapeDataString(_handler.RequestedUris.Single().Query)
            .Should().Contain($"city={expectedCity}&");
    }

    [Fact]
    public async Task GeocodeAsync_HessenCorruptedStrasse_QueriesNominatimWithRepairedStreet()
    {
        _handler.SetDefault(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        });

        await _service.GeocodeAsync(SampleProperty(str: "Adam-Riese-Stra\uFFFDe", ort: "Frankfurt am Main"));

        Uri.UnescapeDataString(_handler.RequestedUris.Single().Query)
            .Should().Contain("street=1 Adam-Riese-Straße&");
    }
}
