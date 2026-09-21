using System.IO.Compression;
using System.Runtime.CompilerServices;
using Grundstuecksfinder.Models;
using Microsoft.Extensions.Options;

namespace Grundstuecksfinder.Services.Importers.Nrw;

/// <summary>Imports NRW's open-data "Grundsteuer" dataset: a JSON manifest pointing at a ZIP of semicolon-CSVs.</summary>
public partial class NrwPropertyImporter(
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
            LogManifestFetchFailed(logger, ex, options.Value.ManifestUrl);
            return [];
        }

        if (manifest?.Datasets is not { Count: > 0 })
        {
            LogManifestEmpty(logger);
            return [];
        }

        var candidates = manifest.Datasets
            .Select(d => (Dataset: d, ZipFile: d.Files.MaxBy(f => f.Timestamp)))
            .Where(x => x.ZipFile is not null)
            .Select(x => new ImportCandidate(x.Dataset.Name, x.ZipFile!.Name, x.ZipFile.Timestamp.ToString("s")))
            .ToList();

        if (candidates.Count == 0)
            LogManifestNoZip(logger);

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
            LogDownloading(logger, url);
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var fs = File.Create(tempFile);
                await response.Content.CopyToAsync(fs, ct);
            }

            LogDownloadComplete(logger);

            await using var zip = await ZipFile.OpenReadAsync(tempFile, ct);
            foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
            {
                LogProcessingEntry(logger, entry.Name);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to fetch manifest from {Url}")]
    private static partial void LogManifestFetchFailed(ILogger logger, Exception exception, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Manifest contained no datasets")]
    private static partial void LogManifestEmpty(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Manifest contained no datasets with a zip file")]
    private static partial void LogManifestNoZip(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloading {Url}...")]
    private static partial void LogDownloading(ILogger logger, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Download complete. Starting import...")]
    private static partial void LogDownloadComplete(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Processing {Entry}...")]
    private static partial void LogProcessingEntry(ILogger logger, string entry);
}
