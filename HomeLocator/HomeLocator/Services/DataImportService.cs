using System.IO.Compression;
using HomeLocator.Data;
using HomeLocator.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace HomeLocator.Services;

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
            logger.LogWarning("Manifest contained no datasets.");
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var dataset in manifest.Datasets)
        {
            var zipFile = dataset.Files.FirstOrDefault(f =>
                f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (zipFile == null) continue;

            var alreadyImported = await context.ImportLogs.AnyAsync(
                l => l.DatasetName == dataset.Name &&
                     l.FileName == zipFile.Name &&
                     l.FileTimestamp == zipFile.Timestamp, ct);

            if (alreadyImported)
            {
                logger.LogInformation("Dataset {Name}/{File} already imported, skipping.", dataset.Name, zipFile.Name);
                continue;
            }

            await ImportZipAsync(context, dataset.Name, zipFile, http, ct);
        }
    }

    private async Task ImportZipAsync(
        AppDbContext context,
        string datasetName,
        ManifestFile zipFile,
        HttpClient http,
        CancellationToken ct)
    {
        var url = $"{BaseDownloadUrl}{datasetName}/{zipFile.Name}";

        // The ZIP can be several GB – download to a temp file first so ZipArchive can seek.
        // Ensure the host has at least 10 GB of free disk space.
        var tempFile = Path.Combine(Path.GetTempPath(), $"homelocator_{Guid.NewGuid():N}.zip");
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

            // Truncate before re-importing. The table will be briefly empty.
            // TODO: For zero-downtime, switch to a staging-table rename strategy.
            await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"Properties\" RESTART IDENTITY", ct);

            long totalRecords = 0;
            using var zip = ZipFile.OpenRead(tempFile);

            foreach (var entry in zip.Entries.Where(e =>
                e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogInformation("Processing {Entry}...", entry.Name);
                await using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                var count = await BulkImportCsvAsync(reader, ct);
                totalRecords += count;
                logger.LogInformation("Imported {Count} records from {Entry}.", count, entry.Name);
            }

            context.ImportLogs.Add(new ImportLog
            {
                DatasetName = datasetName,
                FileName = zipFile.Name,
                FileTimestamp = zipFile.Timestamp,
                ImportedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                RecordCount = totalRecords,
            });
            await context.SaveChangesAsync(ct);
            logger.LogInformation("Import complete. Total records: {Count}.", totalRecords);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import failed for {Url}.", url);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    // Uses Npgsql COPY BINARY – the fastest bulk-insert path into PostgreSQL from .NET.
    private async Task<long> BulkImportCsvAsync(StreamReader reader, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var writer = await conn.BeginBinaryImportAsync(
            "COPY \"Properties\" (\"Str\", \"Hnr\", \"HnrZus\", \"Plz\", \"Ort\", \"Gemeinde\", \"FlaecheAmtl\") FROM STDIN (FORMAT BINARY)");

        long count = 0;
        var headerSkipped = false;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(ct);
            if (line == null) continue;

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
