using Grundstuecksfinder.Data;
using Grundstuecksfinder.Models;
using Microsoft.EntityFrameworkCore;

namespace Grundstuecksfinder.Services.Importers;

/// <summary>
/// Loops every registered <see cref="IPropertyImporter"/>, skipping candidates whose import
/// already completed (see <see cref="ImportLog.CompletedAt"/>), and writes new ones via
/// <see cref="PropertyBulkWriter"/>. One source failing never stops the others; only
/// cancellation of the whole run propagates. Adding a region is registering another
/// <see cref="IPropertyImporter"/> in DI — this class needs no changes.
/// </summary>
public partial class ImportOrchestrator(
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
            // An HttpClient timeout is also an OperationCanceledException; only a cancelled run
            // may propagate, anything else is this source's failure.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogDiscoveryFailed(logger, ex, importer.Source);
            }
        }
    }

    private async Task RunImporterAsync(IPropertyImporter importer, CancellationToken ct)
    {
        LogCheckingForNewData(logger, importer.Source);

        var candidates = await importer.DiscoverAsync(ct);

        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var candidate in candidates)
        {
            var existing = await context.ImportLogs.FirstOrDefaultAsync(
                l => l.Source == importer.Source &&
                     l.DatasetName == candidate.DatasetName &&
                     l.FileName == candidate.FileName &&
                     l.FileTimestamp == candidate.VersionTimestamp, ct);

            if (existing?.CompletedAt is not null)
            {
                LogAlreadyImported(logger, importer.Source, candidate.DatasetName, candidate.FileName);
                continue;
            }

            await ImportCandidateAsync(importer, candidate, existing, context, ct);
        }
    }

    private const int MaxErrorLength = 2000;

    private async Task ImportCandidateAsync(
        IPropertyImporter importer, ImportCandidate candidate, ImportLog? failedEarlier, AppDbContext context, CancellationToken ct)
    {
        // Create (or, for a retry of a failed version, reuse) the ImportLog first so its ID is
        // available for the swap into Properties. It stays incomplete until the swap commits.
        var importLog = failedEarlier ?? context.ImportLogs.Add(new ImportLog
        {
            Source = importer.Source,
            DatasetName = candidate.DatasetName,
            FileName = candidate.FileName,
            FileTimestamp = candidate.VersionTimestamp,
        }).Entity;
        importLog.ImportedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        importLog.RecordCount = 0;
        importLog.LastError = null;
        await context.SaveChangesAsync(ct);

        try
        {
            LogImporting(logger, importer.Source, candidate.DatasetName, candidate.FileName);

            // Marks the ImportLog completed in the same transaction that swaps the rows in.
            var count = await bulkWriter.WriteAsync(importer.Source, importer.FetchAsync(candidate, ct), importLog.Id, ct);
            LogImportComplete(logger, importer.Source, count);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogImportFailed(logger, ex, importer.Source, candidate.DatasetName, candidate.FileName);
            // Surfaced by the import health check until a later attempt succeeds.
            importLog.LastError = ex.Message.Length <= MaxErrorLength ? ex.Message : ex.Message[..MaxErrorLength];
            await context.SaveChangesAsync(ct);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Discovery failed for source {Source}")]
    private static partial void LogDiscoveryFailed(ILogger logger, Exception exception, string source);

    [LoggerMessage(Level = LogLevel.Information, Message = "Checking for new data from {Source}...")]
    private static partial void LogCheckingForNewData(ILogger logger, string source);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dataset {Source}/{Name}/{File} already imported, skipping")]
    private static partial void LogAlreadyImported(ILogger logger, string source, string name, string file);

    [LoggerMessage(Level = LogLevel.Information, Message = "Importing {Source}/{Name}/{File}...")]
    private static partial void LogImporting(ILogger logger, string source, string name, string file);

    [LoggerMessage(Level = LogLevel.Information, Message = "Import complete for {Source}. Total records: {Count}")]
    private static partial void LogImportComplete(ILogger logger, string source, long count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Import failed for {Source}/{Name}/{File}")]
    private static partial void LogImportFailed(ILogger logger, Exception exception, string source, string name, string file);
}
