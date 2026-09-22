using Microsoft.Extensions.Options;

namespace Grundstuecksfinder.Services;

public class GeocodingOptions
{
    /// <summary>
    /// Nominatim base URL: the public instance by default (local development), the self-hosted
    /// container in production ("Geocoding__NominatimBaseUrl" in docker-compose.yml).
    /// </summary>
    public string NominatimBaseUrl { get; set; } = "https://nominatim.openstreetmap.org/";
}

public static class GeocodingServiceCollectionExtensions
{
    /// <summary>The "Nominatim" HttpClient, pointed at <see cref="GeocodingOptions.NominatimBaseUrl"/>, and the geocoder.</summary>
    public static IServiceCollection AddNominatim(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<GeocodingOptions>(configuration.GetSection("Geocoding"));

        // Named client for Nominatim – required User-Agent per usage policy.
        services.AddHttpClient("Nominatim", (sp, client) =>
        {
            client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<GeocodingOptions>>().Value.NominatimBaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Grundstuecksfinder/1.0 (grundstuecksfinder-impressum@meyerweb.eu)");
        });

        services.AddScoped<GeocodingService>();
        return services;
    }
}
