using System.Globalization;
using System.Text.Json.Serialization;
using Grundstuecksfinder.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Grundstuecksfinder.Services;

public class GeocodingService(IHttpClientFactory httpClientFactory, IMemoryCache cache)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

    /// <summary>
    /// Returns WGS-84 coordinates for the given property via Nominatim.
    /// Results are cached for 24 h to respect Nominatim's usage policy.
    /// </summary>
    public async Task<(double Lat, double Lon)?> GeocodeAsync(Property property)
    {
        var cacheKey = $"geo:{property.Str}:{property.Hnr}:{property.Plz}:{property.Ort}";
        if (cache.TryGetValue(cacheKey, out (double Lat, double Lon) cached))
            return cached;

        // Normalize again here so rows imported before the importer cleaned names up
        // ("Stadt Pirna", "Kiel, Landeshauptstadt", "Cottbus [Chóśebuz]") still geocode.
        var str = PlaceNameNormalizer.NormalizeStreet(property.Str);
        var ort = PlaceNameNormalizer.NormalizePlace(property.Ort);

        try
        {
            var http = httpClientFactory.CreateClient("Nominatim");

            Task<(double Lat, double Lon)?> TryAsync(string? city) =>
                SearchAsync(http, BuildQuery(str, property.Hnr, property.Plz, city));

            var coords = await TryAsync(ort);
            // A wrong Ort (e.g. SH's shared postal names: all of Fehmarn as "Petersdorf a. F.")
            // makes Nominatim find nothing; street + postcode alone still pin the address down.
            if (coords is null && !string.IsNullOrWhiteSpace(property.Plz) && ort is not null)
                coords = await TryAsync(null);
            if (coords is null) return null;

            cache.Set(cacheKey, coords.Value, CacheDuration);
            return coords;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(double Lat, double Lon)?> SearchAsync(HttpClient http, string? query)
    {
        if (query is null) return null;

        var results = await http.GetFromJsonAsync<NominatimResult[]>(
            $"search?{query}&format=json&limit=1&countrycodes=de&addressdetails=1");

        var first = results?.FirstOrDefault();
        if (first is null) return null;

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

    private sealed class NominatimResult
    {
        [JsonPropertyName("lat")] public string Lat { get; set; } = "";
        [JsonPropertyName("lon")] public string Lon { get; set; } = "";
        [JsonPropertyName("address")] public Dictionary<string, string>? Address { get; set; }
    }
}
