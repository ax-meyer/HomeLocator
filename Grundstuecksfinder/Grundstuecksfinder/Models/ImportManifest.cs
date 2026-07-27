using System.Text.Json.Serialization;

namespace Grundstuecksfinder.Models;

public class GrundsteuerManifest
{
    [JsonPropertyName("datasets")]
    public List<ManifestDataset> Datasets { get; set; } = [];
}

public class ManifestDataset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<ManifestFile> Files { get; set; } = [];
}

public class ManifestFile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public string Size { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }
}
