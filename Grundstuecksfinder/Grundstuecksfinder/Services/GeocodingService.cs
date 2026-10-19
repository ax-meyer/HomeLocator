using System.Globalization;
using System.Text.Json.Serialization;
using Grundstuecksfinder.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Grundstuecksfinder.Services;

public partial class GeocodingService(IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<GeocodingService> logger)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

    /// <summary>
    /// Returns WGS-84 coordinates of the property's house via Nominatim, or null when Nominatim
    /// doesn't know the house. Results, misses included, are cached for 24 h to respect
    /// Nominatim's usage policy.
    /// </summary>
    public async Task<(double Lat, double Lon)?> GeocodeAsync(Property property)
    {
        // The suffix is part of the address: Berlin's Ackerstraße 3 and 3E are 90 m apart.
        var hnr = $"{property.Hnr}{property.HnrZus?.Trim()}";
        var cacheKey = $"geo:{property.Str}:{hnr}:{property.Plz}:{property.Ort}";
        if (cache.TryGetValue(cacheKey, out (double Lat, double Lon)? cached))
            return cached;

        // Normalize again here so rows imported before the importer cleaned names up
        // ("Stadt Pirna", "Kiel, Landeshauptstadt", "Cottbus [Chóśebuz]") still geocode.
        var str = PlaceNameNormalizer.NormalizeStreet(property.Str);
        var ort = PlaceNameNormalizer.NormalizePlace(property.Ort);

        try
        {
            var http = httpClientFactory.CreateClient("Nominatim");

            Task<(double Lat, double Lon)?> TryAsync(string? city) =>
                SearchAsync(http, BuildQuery(str, hnr, property.Plz, city));

            var coords = await TryAsync(ort);
            // A wrong Ort (e.g. SH's shared postal names: all of Fehmarn as "Petersdorf a. F.")
            // makes Nominatim find nothing; street + postcode alone still pin the address down.
            if (coords is null && !string.IsNullOrWhiteSpace(property.Plz) && ort is not null)
                coords = await TryAsync(null);

            // No third try without the suffix: a pin on no. 3 for 3E would again look exact
            // while marking the wrong building, and the address-search links find 3E itself.
            cache.Set(cacheKey, coords, CacheDuration);
            return coords;
        }
        // The card still works without coordinates (the map links search by address instead),
        // so a failure only costs the pin — but it is logged, or a dead Nominatim goes unnoticed.
        // Without the address: searches aren't recorded (see the Datenschutz page).
        catch (Exception ex)
        {
            LogGeocodingFailed(logger, ex);
            return null;
        }
    }

    private static async Task<(double Lat, double Lon)?> SearchAsync(HttpClient http, string? query)
    {
        if (query is null) return null;

        var results = await http.GetFromJsonAsync<NominatimResult[]>(
            $"search?{query}&format=json&limit=1&countrycodes=de&addressdetails=1");

        // A house Nominatim doesn't know comes back as its street (no house_number), up to
        // hundreds of metres off: Berlin's Ackerstraße 999 landed 700 m from no. 3. As a pin it
        // would look exact, so it counts as no hit and the card searches by address instead.
        var first = results?.FirstOrDefault();
        if (first?.Address?.ContainsKey("house_number") != true) return null;

        return (double.Parse(first.Lat, CultureInfo.InvariantCulture),
                double.Parse(first.Lon, CultureInfo.InvariantCulture));
    }

    private static string? BuildQuery(string? str, string? hnr, string? plz, string? ort)
    {
        var parts = new List<string>();
        if (str is not null && !string.IsNullOrWhiteSpace(hnr))
            parts.Add($"street={Uri.EscapeDataString($"{hnr} {str}")}");
        if (!string.IsNullOrWhiteSpace(plz))
            parts.Add($"postalcode={Uri.EscapeDataString(plz)}");
        if (ort is not null)
            parts.Add($"city={Uri.EscapeDataString(ort)}");

        return parts.Count == 0 ? null : string.Join("&", parts);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Geocoding failed; showing the card without coordinates")]
    private static partial void LogGeocodingFailed(ILogger logger, Exception exception);

    private sealed class NominatimResult
    {
        [JsonPropertyName("lat")] public string Lat { get; set; } = "";
        [JsonPropertyName("lon")] public string Lon { get; set; } = "";
        [JsonPropertyName("address")] public Dictionary<string, string>? Address { get; set; }
    }
}
