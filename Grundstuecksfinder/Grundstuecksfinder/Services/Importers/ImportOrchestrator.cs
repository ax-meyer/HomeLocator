using System.Runtime.CompilerServices;
using Grundstuecksfinder.Data;
using Grundstuecksfinder.Models;
using Microsoft.EntityFrameworkCore;

namespace Grundstuecksfinder.Services.Importers;

/// <summary>
/// Loops every registered <see cref="IPropertyImporter"/>, skipping candidates whose version is
/// already the source's served run (see <see cref="SourceState.ServedRun"/>), and writes new
/// ones via <see cref="PropertyBulkWriter"/>. One source failing never stops the others; only
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
            var fingerprint = $"{candidate.DatasetName}/{candidate.FileName}/{candidate.VersionTimestamp}";
            var state = await context.SourceStates.Include(s => s.ServedRun)
                .FirstOrDefaultAsync(s => s.Source == importer.Source, ct);

            if (state?.ServedRun?.Fingerprint == fingerprint)
            {
                LogAlreadyImported(logger, importer.Source, candidate.DatasetName, candidate.FileName);
                // The data is still current as of now; shown to users instead of the (possibly
                // old) import date, so unchanged data doesn't look stale.
                state.LastCheckedAt = DateTimeOffset.UtcNow;
                await context.SaveChangesAsync(ct);
                continue;
            }

            var reason = state?.ServedRun is null ? ImportReason.Initial : ImportReason.Changed;
            await ImportCandidateAsync(importer, candidate, fingerprint, reason, context, ct);
        }
    }

    private const int MaxErrorLength = 2000;

    private async Task ImportCandidateAsync(
        IPropertyImporter importer, ImportCandidate candidate, string fingerprint, ImportReason reason,
        AppDbContext context, CancellationToken ct)
    {
        // Created first so its ID is available for the swap into Properties. It stays
        // incomplete until the swap commits.
        var run = context.ImportRuns.Add(new ImportRun
        {
            Source = importer.Source,
            Fingerprint = fingerprint,
            Reason = reason,
            StartedAt = DateTimeOffset.UtcNow,
        }).Entity;
        await context.SaveChangesAsync(ct);

        try
        {
            LogImporting(logger, importer.Source, candidate.DatasetName, candidate.FileName);

            // Completes the run in the same transaction that swaps the rows in.
            var runContext = new ImportRunContext(run.Id, importer.Source);
            var count = await bulkWriter.WriteAsync(runContext, ReportingSkippedTiles(importer, candidate, runContext, ct), ct);
            LogImportComplete(logger, importer.Source, count);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogImportFailed(logger, ex, importer.Source, candidate.DatasetName, candidate.FileName);
            // Surfaced by the import health check until a later attempt succeeds.
            run.FailedAt = DateTimeOffset.UtcNow;
            run.Error = ex.Message.Length <= MaxErrorLength ? ex.Message : ex.Message[..MaxErrorLength];
            await context.SaveChangesAsync(ct);
        }
    }

    /// <summary>The importer's rows, then the tiles it skipped — only known once they've all been read.</summary>
    private static async IAsyncEnumerable<Property> ReportingSkippedTiles(
        IPropertyImporter importer, ImportCandidate candidate, ImportRunContext run, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var property in importer.FetchAsync(candidate, ct).WithCancellation(ct))
            yield return property;
        run.AddSkippedParts(importer.SkippedTiles);
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
