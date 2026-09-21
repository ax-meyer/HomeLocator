using System.Net;
using System.Text;
using FakeItEasy;
using Xunit;
using FluentAssertions;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Grundstuecksfinder.Tests;

public sealed class GeocodingServiceTests : IDisposable
{
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly MemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly GeocodingOptions _options = new();
    private readonly GeocodingService _service;

    public GeocodingServiceTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("https://nominatim.openstreetmap.org/") };
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient("Nominatim")).Returns(http);
        _service = new GeocodingService(factory, _cache, Options.Create(_options));
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

        Uri.UnescapeDataString(_handler.RequestedUris[0].Query)
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

        Uri.UnescapeDataString(_handler.RequestedUris[0].Query)
            .Should().Contain("street=1 Adam-Riese-Straße&");
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

    private static HttpResponseMessage NominatimHit(string road) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$$"""[{"lat":"50.1","lon":"8.6","address":{"house_number":"1","road":"{{{road}}}","city":"Frankfurt am Main"}}]""",
            Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage NominatimMiss() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("[]", Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task GeocodeAsync_RepairEnabled_TriesSpellingsUntilNominatimConfirmsOne()
    {
        _options.RepairCorruptedStreets = true;
        _handler.SetDefault(() =>
        {
            var query = Uri.UnescapeDataString(_handler.RequestedUris[^1].Query);
            return query.Contains("Am Mühlgraben") ? NominatimHit("Am Mühlgraben") : NominatimMiss();
        });

        var result = await _service.GeocodeAsync(SampleProperty(str: "Am M\uFFFDhlgraben", ort: "Frankfurt am Main"));

        result.Should().NotBeNull();
        _handler.RequestedUris.Should().HaveCount(1, "ü is the most likely letter after a consonant");
    }

    [Fact]
    public async Task GeocodeAsync_RepairEnabled_RejectsHitWhoseStreetDiffersFromCandidate()
    {
        _options.RepairCorruptedStreets = true;
        // Nominatim matching loosely and returning some other street must not count as confirmation.
        _handler.SetDefault(() => NominatimHit("Hauptstraße"));

        var result = await _service.GeocodeAsync(SampleProperty(str: "Am M\uFFFDhlgraben", ort: "Frankfurt am Main"));

        result.Should().BeNull();
        _handler.RequestedUris.Should().HaveCount(10,
            "every single-letter candidate is tried, then all of them again without the city");
    }

    [Fact]
    public async Task GeocodeAsync_RepairDisabled_SendsOneRequestOnly()
    {
        _handler.SetDefault(NominatimMiss);

        await _service.GeocodeAsync(SampleProperty(str: "Am M\uFFFDhlgraben", ort: "Frankfurt am Main"));

        _handler.RequestedUris.Should().HaveCount(2,
            "the public Nominatim must not get a burst of guesses — only the plain city-less retry");
    }
}
