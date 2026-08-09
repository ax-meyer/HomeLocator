using System.Net.Http.Json;
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

        var query = BuildQuery(property);
        if (query is null) return null;

        try
        {
            var http = httpClientFactory.CreateClient("Nominatim");
            var results = await http.GetFromJsonAsync<NominatimResult[]>(
                $"search?{query}&format=json&limit=1&countrycodes=de");

            var first = results?.FirstOrDefault();
            if (first is null) return null;

            var coords = (double.Parse(first.Lat, System.Globalization.CultureInfo.InvariantCulture),
                          double.Parse(first.Lon, System.Globalization.CultureInfo.InvariantCulture));

            cache.Set(cacheKey, coords, CacheDuration);
            return coords;
        }
        catch
        {
            return null;
        }
    }

    private static string? BuildQuery(Property p)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.Str) && !string.IsNullOrWhiteSpace(p.Hnr))
            parts.Add($"street={Uri.EscapeDataString($"{p.Hnr} {p.Str}")}");
        if (!string.IsNullOrWhiteSpace(p.Plz))
            parts.Add($"postalcode={Uri.EscapeDataString(p.Plz)}");
        if (!string.IsNullOrWhiteSpace(p.Ort))
            parts.Add($"city={Uri.EscapeDataString(p.Ort)}");

        return parts.Count == 0 ? null : string.Join("&", parts);
    }

    private sealed class NominatimResult
    {
        [JsonPropertyName("lat")] public string Lat { get; set; } = "";
        [JsonPropertyName("lon")] public string Lon { get; set; } = "";
    }
}
