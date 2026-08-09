using Grundstuecksfinder.Data;
using Grundstuecksfinder.Models;
using Microsoft.EntityFrameworkCore;

namespace Grundstuecksfinder.Services.Importers;

/// <summary>
/// Loops every registered <see cref="IPropertyImporter"/>, skipping candidates already
/// recorded in <see cref="ImportLog"/> for that source, and writes new ones via
/// <see cref="PropertyBulkWriter"/>. Adding a region is registering another
/// <see cref="IPropertyImporter"/> in DI — this class needs no changes.
/// </summary>
public class ImportOrchestrator(
    IEnumerable<IPropertyImporter> importers,
    ILogger<ImportOrchestrator> logger,
    IServiceScopeFactory scopeFactory,
    PropertyBulkWriter bulkWriter)
{
    public async Task CheckAndImportAsync(CancellationToken ct = default)
    {
        foreach (var importer in importers)
        {
            try
            {
                await RunImporterAsync(importer, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Discovery failed for source {Source}", importer.Source);
            }
        }
    }

    private async Task RunImporterAsync(IPropertyImporter importer, CancellationToken ct)
    {
        logger.LogInformation("Checking for new data from {Source}...", importer.Source);

        var candidates = await importer.DiscoverAsync(ct);

        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var candidate in candidates)
        {
            var alreadyImported = await context.ImportLogs.AnyAsync(
                l => l.Source == importer.Source &&
                     l.DatasetName == candidate.DatasetName &&
                     l.FileName == candidate.FileName &&
                     l.FileTimestamp == candidate.VersionTimestamp, ct);

            if (alreadyImported)
            {
                logger.LogInformation("Dataset {Source}/{Name}/{File} already imported, skipping",
                    importer.Source, candidate.DatasetName, candidate.FileName);
                continue;
            }

            await ImportCandidateAsync(importer, candidate, context, ct);
        }
    }

    private async Task ImportCandidateAsync(IPropertyImporter importer, ImportCandidate candidate, AppDbContext context, CancellationToken ct)
    {
        // Create the ImportLog first so its ID is available for the COPY command.
        var importLog = new ImportLog
        {
            Source = importer.Source,
            DatasetName = candidate.DatasetName,
            FileName = candidate.FileName,
            FileTimestamp = candidate.VersionTimestamp,
            ImportedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            RecordCount = 0,
        };
        context.ImportLogs.Add(importLog);
        await context.SaveChangesAsync(ct);

        try
        {
            logger.LogInformation("Importing {Source}/{Name}/{File}...", importer.Source, candidate.DatasetName, candidate.FileName);

            var count = await bulkWriter.WriteAsync(importer.Source, importer.FetchAsync(candidate, ct), importLog.Id, ct);

            importLog.RecordCount = count;
            await context.SaveChangesAsync(ct);
            logger.LogInformation("Import complete for {Source}. Total records: {Count}", importer.Source, count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import failed for {Source}/{Name}/{File}", importer.Source, candidate.DatasetName, candidate.FileName);
        }
    }
}
