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
        string plz = "50667", string ort = "Köln", string? hnrZus = null) =>
        new() { Str = str, Hnr = hnr, HnrZus = hnrZus, Plz = plz, Ort = ort, Gemeinde = ort };

    private static Func<HttpResponseMessage> Json(string body) => () =>
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    /// <summary>A Nominatim hit on a house, as <c>addressdetails=1</c> returns it.</summary>
    private static Func<HttpResponseMessage> HouseHit(string lat, string lon, string houseNumber = "1") =>
        Json($$$"""[{"lat":"{{{lat}}}","lon":"{{{lon}}}","addresstype":"building","address":{"house_number":"{{{houseNumber}}}","road":"Hauptstraße"}}]""");

    private string Street(int request) =>
        System.Web.HttpUtility.ParseQueryString(_handler.RequestedUris[request].Query)["street"]!;

    [Fact]
    public async Task GeocodeAsync_ValidAddress_ReturnsCoordinates()
    {
        _handler.SetDefault(HouseHit("50.938361", "6.959974"));

        var result = await _service.GeocodeAsync(SampleProperty());

        result.Should().NotBeNull();
        result!.Value.Lat.Should().BeApproximately(50.938361, 0.0001);
        result.Value.Lon.Should().BeApproximately(6.959974, 0.0001);
        _handler.RequestedUris.Should().ContainSingle("a hit needs no retry");
    }

    [Fact]
    public async Task GeocodeAsync_EmptyResult_ReturnsNull()
    {
        _handler.SetDefault(Json("[]"));

        var result = await _service.GeocodeAsync(SampleProperty());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GeocodeAsync_StreetLevelResult_ReturnsNull()
    {
        // What Nominatim returned for Ackerstraße 999 in Berlin-Mitte: the street, 700 m off.
        _handler.SetDefault(Json(
            """[{"lat":"52.5349549","lon":"13.3911362","addresstype":"road","address":{"road":"Ackerstraße","postcode":"10115"}}]"""));

        var result = await _service.GeocodeAsync(
            SampleProperty(str: "Ackerstraße", hnr: "999", plz: "10115", ort: "Mitte"));

        result.Should().BeNull("a street-level point would show as an exact pin");
        _handler.RequestedUris.Should().HaveCount(2, "a street-level hit is a miss, so the retry without city runs");
    }

    [Fact]
    public async Task GeocodeAsync_ResultWithoutAddressDetails_ReturnsNull()
    {
        _handler.SetDefault(Json("""[{"lat":"50.938361","lon":"6.959974"}]"""));

        var result = await _service.GeocodeAsync(SampleProperty());

        result.Should().BeNull("without a house_number the hit can't be told from a street");
    }

    [Fact]
    public async Task GeocodeAsync_StreetLevelWithCity_RetriesAndAcceptsHouseWithoutCity()
    {
        _handler.SetDefault(Json(
            """[{"lat":"54.42","lon":"11.27","addresstype":"road","address":{"road":"Achter de Höf"}}]"""));
        _handler.AddRoute(
            new Uri("https://nominatim.openstreetmap.org/search?street=14%20Achter%20de%20H%C3%B6f&postalcode=23769&format=json&limit=1&countrycodes=de&addressdetails=1").ToString(),
            HouseHit("54.4155466", "11.2706687", "14"));

        var result = await _service.GeocodeAsync(
            SampleProperty(str: "Achter de Höf", hnr: "14", plz: "23769", ort: "Petersdorf a. F."));

        result!.Value.Lat.Should().BeApproximately(54.4155466, 0.0001);
        _handler.RequestedUris.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("A", "3A Ackerstraße")]
    [InlineData("a", "3a Ackerstraße")]
    [InlineData(" E", "3E Ackerstraße")]
    public async Task GeocodeAsync_HouseNumberSuffix_IsPartOfTheStreetQuery(string hnrZus, string expectedStreet)
    {
        _handler.SetDefault(HouseHit("52.5300653", "13.3979283", "3A"));

        await _service.GeocodeAsync(
            SampleProperty(str: "Ackerstraße", hnr: "3", hnrZus: hnrZus, plz: "10115", ort: "Mitte"));

        Street(0).Should().Be(expectedStreet);
    }

    [Fact]
    public async Task GeocodeAsync_SuffixNotFound_DoesNotRetryWithoutSuffix()
    {
        _handler.SetDefault(Json(
            """[{"lat":"52.5351309","lon":"13.3907781","addresstype":"road","address":{"road":"Ackerstraße"}}]"""));

        var result = await _service.GeocodeAsync(
            SampleProperty(str: "Ackerstraße", hnr: "3", hnrZus: "Z", plz: "10115", ort: "Mitte"));

        result.Should().BeNull("a pin on no. 3 would mark the wrong building; the links search for 3Z");
        _handler.RequestedUris.Select((_, i) => Street(i)).Should().AllBe("3Z Ackerstraße");
    }

    [Fact]
    public async Task GeocodeAsync_SameNumberDifferentSuffix_IsCachedSeparately()
    {
        // Berlin's Ackerstraße 3, 3A and 3E all got no. 3's pin while the suffix was ignored.
        _handler.SetDefault(HouseHit("52.5299984", "13.3976003", "3"));
        _handler.AddRoute(
            new Uri("https://nominatim.openstreetmap.org/search?street=3A%20Ackerstra%C3%9Fe&postalcode=10115&city=Mitte&format=json&limit=1&countrycodes=de&addressdetails=1").ToString(),
            HouseHit("52.5300653", "13.3979283", "3A"));

        var three = await _service.GeocodeAsync(
            SampleProperty(str: "Ackerstraße", hnr: "3", plz: "10115", ort: "Mitte"));
        var threeA = await _service.GeocodeAsync(
            SampleProperty(str: "Ackerstraße", hnr: "3", hnrZus: "A", plz: "10115", ort: "Mitte"));

        _handler.RequestedUris.Should().HaveCount(2, "3A must not be served from 3's cache entry");
        threeA.Should().NotBe(three);
        threeA!.Value.Lon.Should().BeApproximately(13.3979283, 0.0000001);
    }

    [Fact]
    public async Task GeocodeAsync_Miss_IsCached()
    {
        _handler.SetDefault(Json("[]"));

        await _service.GeocodeAsync(SampleProperty());
        var requests = _handler.RequestedUris.Count;
        var second = await _service.GeocodeAsync(SampleProperty());

        second.Should().BeNull();
        _handler.RequestedUris.Should().HaveCount(requests, "reopening a card Nominatim can't place asks again otherwise");
    }

    [Fact]
    public async Task GeocodeAsync_HttpFailure_IsNotCached()
    {
        _handler.SetDefault(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await _service.GeocodeAsync(SampleProperty());

        _handler.SetDefault(HouseHit("50.938361", "6.959974"));
        var result = await _service.GeocodeAsync(SampleProperty());

        result.Should().NotBeNull("an outage must not hide the pin for a day");
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
        _handler.SetDefault(Json("[]"));

        await _service.GeocodeAsync(SampleProperty());

        _logger.Entries.Should().BeEmpty("an address Nominatim doesn't know is no failure");
    }

    [Fact]
    public async Task GeocodeAsync_SameAddress_ReturnsCachedResult()
    {
        var callCount = 0;
        var hit = HouseHit("51.514244", "7.468429");
        _handler.SetDefault(() =>
        {
            callCount++;
            return hit();
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
        _handler.SetDefault(Json("[]"));

        await _service.GeocodeAsync(SampleProperty(ort: ort));

        Uri.UnescapeDataString(_handler.RequestedUris[0].Query)
            .Should().Contain($"city={expectedCity}&");
    }

    [Fact]
    public async Task GeocodeAsync_NoHitWithCity_RetriesWithStreetAndPostcodeOnly()
    {
        _handler.SetDefault(Json("[]"));
        _handler.AddRoute(
            new Uri("https://nominatim.openstreetmap.org/search?street=14%20Achter%20de%20H%C3%B6f&postalcode=23769&format=json&limit=1&countrycodes=de&addressdetails=1").ToString(),
            HouseHit("54.4155466", "11.2706687", "14"));

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
        _handler.SetDefault(Json("[]"));

        var result = await _service.GeocodeAsync(new Property { Str = "Zeil", Hnr = "1", Ort = "Frankfurt am Main" });

        result.Should().BeNull();
        _handler.RequestedUris.Should().ContainSingle("street alone would match the same street anywhere in Germany");
    }
}
