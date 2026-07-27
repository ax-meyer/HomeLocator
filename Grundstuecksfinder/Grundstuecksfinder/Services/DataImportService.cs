using System.IO.Compression;
using Grundstuecksfinder.Data;
using Grundstuecksfinder.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Grundstuecksfinder.Services;

public class DataImportService(
    ILogger<DataImportService> logger,
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    NpgsqlDataSource dataSource,
    IConfiguration configuration)
{
    private string ManifestUrl => configuration["Import:ManifestUrl"]
        ?? "https://www.opengeodata.nrw.de/produkte/infrastruktur_bauen_wohnen/grundsteuer/index.json";

    private string BaseDownloadUrl => configuration["Import:BaseDownloadUrl"]
        ?? "https://www.opengeodata.nrw.de/produkte/infrastruktur_bauen_wohnen/grundsteuer/";

    public async Task CheckAndImportAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Checking for new Grundsteuer dataset...");

        var http = httpClientFactory.CreateClient();
        GrundsteuerManifest? manifest;
        try
        {
            manifest = await http.GetFromJsonAsync<GrundsteuerManifest>(ManifestUrl, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch manifest from {Url}", ManifestUrl);
            return;
        }

        if (manifest?.Datasets is not { Count: > 0 })
        {
            logger.LogWarning("Manifest contained no datasets");
            return;
        }

        var newestFiles = manifest.Datasets
            .Select(d => (Dataset: d, ZipFile: d.Files.MaxBy(f => f.Timestamp)))
            .ToList();

        if (newestFiles.Any(f => f.ZipFile is null))
        {
            logger.LogWarning("Manifest contained no datasets with a zip file");
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var (dataset, zipFile) in newestFiles)
        {
            var alreadyImported = await context.ImportLogs.AnyAsync(
                l => l.DatasetName == dataset.Name &&
                     l.FileName == zipFile!.Name &&
                     l.FileTimestamp == zipFile.Timestamp.ToString("s"), ct);

            if (alreadyImported)
            {
                logger.LogInformation("Dataset {Name}/{File} already imported, skipping", dataset.Name, zipFile!.Name);
                continue;
            }

            await ImportZipAsync(context, dataset.Name, zipFile!, http, ct);
        }
    }

    private async Task ImportZipAsync(
        AppDbContext context,
        string datasetName,
        ManifestFile zipFile,
        HttpClient http,
        CancellationToken ct)
    {
        var url = $"{BaseDownloadUrl}/{zipFile.Name}";

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

            // Create the ImportLog first so its ID is available for the COPY command.
            var importLog = new ImportLog
            {
                DatasetName = datasetName,
                FileName = zipFile.Name,
                FileTimestamp = zipFile.Timestamp.ToString("s"),
                ImportedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                RecordCount = 0,
            };
            context.ImportLogs.Add(importLog);
            await context.SaveChangesAsync(ct);

            // Truncate before re-importing. The table will be briefly empty.
            // TODO: For zero-downtime, switch to a staging-table rename strategy.
            await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"Properties\" RESTART IDENTITY", ct);

            long totalRecords = 0;
            await using var zip =  await ZipFile.OpenReadAsync(tempFile, ct);

            foreach (var entry in zip.Entries.Where(e =>
                e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogInformation("Processing {Entry}...", entry.Name);
                await using var stream = await entry.OpenAsync(ct);
                using var reader = new StreamReader(stream);
                var count = await BulkImportCsvAsync(reader, importLog.Id, ct);
                totalRecords += count;
                logger.LogInformation("Imported {Count} records from {Entry}", count, entry.Name);
            }

            importLog.RecordCount = totalRecords;
            await context.SaveChangesAsync(ct);
            logger.LogInformation("Import complete. Total records: {Count}", totalRecords);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import failed for {Url}", url);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    // Uses Npgsql COPY BINARY – the fastest bulk-insert path into PostgreSQL from .NET.
    private async Task<long> BulkImportCsvAsync(StreamReader reader, int importLogId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var writer = await conn.BeginBinaryImportAsync(
            "COPY \"Properties\" (\"Str\", \"Hnr\", \"HnrZus\", \"Plz\", \"Ort\", \"Gemeinde\", \"FlaecheAmtl\", \"ImportLogId\") FROM STDIN (FORMAT BINARY)", ct);

        long count = 0;
        var headerSkipped = false;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            ct.ThrowIfCancellationRequested();

            if (!headerSkipped) { headerSkipped = true; continue; }

            var property = CsvParser.ParseLine(line);
            if (property == null) continue;

            await writer.StartRowAsync(ct);
            await WriteNullableTextAsync(writer, property.Str, ct);
            await WriteNullableTextAsync(writer, property.Hnr, ct);
            await WriteNullableTextAsync(writer, property.HnrZus, ct);
            await WriteNullableTextAsync(writer, property.Plz, ct);
            await WriteNullableTextAsync(writer, property.Ort, ct);
            await WriteNullableTextAsync(writer, property.Gemeinde, ct);

            if (property.FlaecheAmtl.HasValue)
                await writer.WriteAsync(property.FlaecheAmtl.Value, NpgsqlDbType.Double, ct);
            else
                await writer.WriteNullAsync(ct);

            await writer.WriteAsync(importLogId, NpgsqlDbType.Integer, ct);

            count++;
        }

        await writer.CompleteAsync(ct);
        return count;
    }

    private static async Task WriteNullableTextAsync(NpgsqlBinaryImporter writer, string? value, CancellationToken ct)
    {
        if (value != null)
            await writer.WriteAsync(value, NpgsqlDbType.Text, ct);
        else
            await writer.WriteNullAsync(ct);
    }
}
