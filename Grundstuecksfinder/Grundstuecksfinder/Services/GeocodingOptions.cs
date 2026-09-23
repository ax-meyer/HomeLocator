using Microsoft.Extensions.Options;
using Grundstuecksfinder.Infrastructure;

namespace Grundstuecksfinder.Services;

public class GeocodingOptions
{
    /// <summary>
    /// Nominatim base URL: the public instance by default (local development), the self-hosted
    /// container in production ("Geocoding__NominatimBaseUrl" in docker-compose.yml).
    /// </summary>
    public string NominatimBaseUrl { get; set; } = "https://nominatim.openstreetmap.org/";

    /// <summary>
    /// Guess the letters Hessen's WFS replaced with U+FFFD and keep the first spelling Nominatim
    /// confirms. Costs up to <see cref="MaxRepairCandidates"/> requests per address, so only
    /// enable it against a self-hosted Nominatim — the public one allows 1 request/s.
    /// </summary>
    public bool RepairCorruptedStreets { get; set; }

    public int MaxRepairCandidates { get; set; } = 16;
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
            client.DefaultRequestHeaders.UserAgent.ParseAdd(OutboundHttp.UserAgent);
        });

        services.AddScoped<GeocodingService>();
        return services;
    }
}
