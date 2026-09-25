using System.Net;
using System.Text;
using FakeItEasy;
using Xunit;
using FluentAssertions;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Grundstuecksfinder.Tests;

public sealed class GeocodingServiceTests : IDisposable
{
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly MemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly CapturingLogger<GeocodingService> _logger = new();
    private readonly GeocodingService _service;

    public GeocodingServiceTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("https://nominatim.openstreetmap.org/") };
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient("Nominatim")).Returns(http);
        _service = new GeocodingService(factory, _cache, _logger);
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
        _handler.RequestedUris.Should().ContainSingle("a hit needs no retry");
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
    public async Task GeocodeAsync_HttpFailure_ReturnsNullAndLogsWithoutTheAddress()
    {
        _handler.SetDefault(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await _service.GeocodeAsync(SampleProperty());

        result.Should().BeNull();
        _logger.MessagesAt(LogLevel.Warning).Should().ContainSingle()
            .Which.Should().NotContain("Hauptstraße", "searches aren't recorded");
    }

    [Fact]
    public async Task GeocodeAsync_NoHit_LogsNothing()
    {
        _handler.SetDefault(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        });

        await _service.GeocodeAsync(SampleProperty());

        _logger.Entries.Should().BeEmpty("an address Nominatim doesn't know is no failure");
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

        Uri.UnescapeDataString(_handler.RequestedUris[0].Query)
            .Should().Contain($"city={expectedCity}&");
    }

    [Fact]
    public async Task GeocodeAsync_NoHitWithCity_RetriesWithStreetAndPostcodeOnly()
    {
        _handler.SetDefault(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        });
        _handler.AddRoute(
            new Uri("https://nominatim.openstreetmap.org/search?street=14%20Achter%20de%20H%C3%B6f&postalcode=23769&format=json&limit=1&countrycodes=de&addressdetails=1").ToString(),
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"lat":"54.4155466","lon":"11.2706687"}]""", Encoding.UTF8, "application/json")
            });

        var result = await _service.GeocodeAsync(
            SampleProperty(str: "Achter de Höf", hnr: "14", plz: "23769", ort: "Petersdorf a. F."));

        result.Should().NotBeNull();
        result!.Value.Lat.Should().BeApproximately(54.4155466, 0.0001);
        _handler.RequestedUris.Should().HaveCount(2);
        Uri.UnescapeDataString(_handler.RequestedUris[0].Query).Should().Contain("city=Petersdorf a. F.&");
        _handler.RequestedUris[1].Query.Should().NotContain("city=");
    }

    [Fact]
    public async Task GeocodeAsync_NoHitWithoutPostcode_DoesNotRetryWithoutCity()
    {
        _handler.SetDefault(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        });

        var result = await _service.GeocodeAsync(new Property { Str = "Zeil", Hnr = "1", Ort = "Frankfurt am Main" });

        result.Should().BeNull();
        _handler.RequestedUris.Should().ContainSingle("street alone would match the same street anywhere in Germany");
    }
}
