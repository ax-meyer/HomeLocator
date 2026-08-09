using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Grundstuecksfinder.Models;
using Microsoft.Extensions.Options;

namespace Grundstuecksfinder.Services.Importers.Nrw;

/// <summary>Imports NRW's open-data "Grundsteuer" dataset: a JSON manifest pointing at a ZIP of semicolon-CSVs.</summary>
public class NrwPropertyImporter(
    ILogger<NrwPropertyImporter> logger,
    IHttpClientFactory httpClientFactory,
    IOptions<NrwImporterOptions> options) : IPropertyImporter
{
    public const string SourceId = "nrw";

    public string Source => SourceId;

    // CSV columns (0-indexed): id=0, str=4, hnr=5, hnr_zus=6, plz=7, ort=8, gemeinde=9, flaeche_amtl=16
    public static readonly CsvColumnMap ColumnMap = new(
        Delimiter: ';',
        MinColumnCount: 17,
        StrIndex: 4,
        HnrIndex: 5,
        HnrZusIndex: 6,
        PlzIndex: 7,
        OrtIndex: 8,
        GemeindeIndex: 9,
        FlaecheAmtlIndex: 16);

    public async Task<IReadOnlyList<ImportCandidate>> DiscoverAsync(CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient();
        GrundsteuerManifest? manifest;
        try
        {
            manifest = await http.GetFromJsonAsync<GrundsteuerManifest>(options.Value.ManifestUrl, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch manifest from {Url}", options.Value.ManifestUrl);
            return [];
        }

        if (manifest?.Datasets is not { Count: > 0 })
        {
            logger.LogWarning("Manifest contained no datasets");
            return [];
        }

        var candidates = manifest.Datasets
            .Select(d => (Dataset: d, ZipFile: d.Files.MaxBy(f => f.Timestamp)))
            .Where(x => x.ZipFile is not null)
            .Select(x => new ImportCandidate(x.Dataset.Name, x.ZipFile!.Name, x.ZipFile.Timestamp.ToString("s")))
            .ToList();

        if (candidates.Count == 0)
            logger.LogWarning("Manifest contained no datasets with a zip file");

        return candidates;
    }

    public async IAsyncEnumerable<Property> FetchAsync(ImportCandidate candidate, [EnumeratorCancellation] CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient();
        var url = $"{options.Value.BaseDownloadUrl.TrimEnd('/')}/{candidate.FileName}";

        // The ZIP can be several GB – download to a temp file first so ZipArchive can seek.
        // Ensure the host has at least 10 GB of free disk space.
        var tempFile = Path.Combine(Path.GetTempPath(), $"grundstuecksfinder_{Guid.NewGuid():N}.zip");
        try
        {
            logger.LogInformation("Downloading {Url}...", url);
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var fs = File.Create(tempFile);
                await response.Content.CopyToAsync(fs, ct);
            }

            logger.LogInformation("Download complete. Starting import...");

            await using var zip = await ZipFile.OpenReadAsync(tempFile, ct);
            foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogInformation("Processing {Entry}...", entry.Name);
                await using var stream = await entry.OpenAsync(ct);
                using var reader = new StreamReader(stream);

                var headerSkipped = false;
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!headerSkipped) { headerSkipped = true; continue; }

                    var property = DelimitedPropertyParser.ParseLine(line, ColumnMap);
                    if (property != null) yield return property;
                }
            }
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
