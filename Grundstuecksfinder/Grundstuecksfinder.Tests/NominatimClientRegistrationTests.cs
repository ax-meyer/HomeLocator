using FluentAssertions;
using Grundstuecksfinder.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Grundstuecksfinder.Tests;

public sealed class NominatimClientRegistrationTests
{
    private static HttpClient NominatimClient(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddNominatim(configuration).BuildServiceProvider();
        return services.GetRequiredService<IHttpClientFactory>().CreateClient("Nominatim");
    }

    [Fact]
    public void NoGeocodingConfig_UsesThePublicInstance() =>
        NominatimClient([]).BaseAddress.Should().Be(new Uri("https://nominatim.openstreetmap.org/"));

    [Fact]
    public void ConfiguredBaseUrl_IsUsed()
    {
        // As docker-compose.yml sets it for the self-hosted container.
        var client = NominatimClient(new() { ["Geocoding:NominatimBaseUrl"] = "http://nominatim:8080/" });

        client.BaseAddress.Should().Be(new Uri("http://nominatim:8080/"));
        client.DefaultRequestHeaders.UserAgent.ToString().Should().StartWith("Grundstuecksfinder/1.0");
    }
}
