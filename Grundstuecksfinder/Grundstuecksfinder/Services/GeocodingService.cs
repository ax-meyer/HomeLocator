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

        try
        {
            var http = httpClientFactory.CreateClient("Nominatim");
            var coords = await SearchAsync(http, BuildQuery(property, includeCity: true));
            // A wrong Ort (e.g. SH's shared postal names: all of Fehmarn as "Petersdorf a. F.")
            // makes Nominatim find nothing; street + postcode alone still pin the address down.
            if (coords is null && HasPostcodeAndCity(property))
                coords = await SearchAsync(http, BuildQuery(property, includeCity: false));
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
            $"search?{query}&format=json&limit=1&countrycodes=de");

        var first = results?.FirstOrDefault();
        if (first is null) return null;

        return (double.Parse(first.Lat, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(first.Lon, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool HasPostcodeAndCity(Property p) =>
        !string.IsNullOrWhiteSpace(p.Plz) && PlaceNameNormalizer.NormalizePlace(p.Ort) is not null;

    private static string? BuildQuery(Property p, bool includeCity)
    {
        // Normalize again here so rows imported before the importer cleaned names up
        // ("Stadt Pirna", "Kiel, Landeshauptstadt", "Cottbus [Chóśebuz]") still geocode.
        var str = PlaceNameNormalizer.RepairStreet(p.Str);
        var ort = PlaceNameNormalizer.NormalizePlace(p.Ort);

        var parts = new List<string>();
        if (str is not null && !string.IsNullOrWhiteSpace(p.Hnr))
            parts.Add($"street={Uri.EscapeDataString($"{p.Hnr} {str}")}");
        if (!string.IsNullOrWhiteSpace(p.Plz))
            parts.Add($"postalcode={Uri.EscapeDataString(p.Plz)}");
        if (includeCity && ort is not null)
            parts.Add($"city={Uri.EscapeDataString(ort)}");

        return parts.Count == 0 ? null : string.Join("&", parts);
    }

    private sealed class NominatimResult
    {
        [JsonPropertyName("lat")] public string Lat { get; set; } = "";
        [JsonPropertyName("lon")] public string Lon { get; set; } = "";
    }
}
