using Grundstuecksfinder.Models;
using Npgsql;

namespace Grundstuecksfinder.Services.Importers.Scheduling;

/// <summary>
/// One import run over every registered <see cref="IPropertySource"/>: probe them all, let
/// <see cref="RefreshPlanner"/> decide what is due, then import that one source after another
/// through <see cref="PropertyBulkWriter"/>, recording each attempt as an
/// <see cref="ImportRun"/>. A source whose probe or import fails is logged and recorded, and
/// the run moves on to the next; only cancellation of the whole run propagates.
/// </summary>
/// <remarks>
/// <para>
/// Sources are imported sequentially on purpose: the large ones run for hours against public
/// services that are asked to be used gently, and the database takes one swap at a time anyway.
/// </para>
/// <para>
/// A whole run holds a database-wide lock, so two instances (a deploy overlap, a dev machine
/// pointed at prod) never plan and record at the same time; the one that finds it taken skips
/// its run without recording anything. Holding it also means no other run can be in progress,
/// so a run found unfinished was cut short by a crash or a shutdown and is recorded as failed.
/// </para>
/// </remarks>
public sealed partial class ImportRunner(
    IEnumerable<IPropertySource> sources,
    ImportStateStore store,
    PropertyBulkWriter writer,
    NpgsqlDataSource dataSource,
    RefreshOptions options,
    TimeProvider time,
    ILogger<ImportRunner> logger)
{
    /// <summary>The advisory lock scope and key a whole run holds.</summary>
    public const string RunLockScope = "property-import-run";
    public const string RunLockKey = "all-sources";

    public async Task RunAsync(CancellationToken ct)
    {
        await using var runLock = await AdvisoryLock.TryAcquireAsync(dataSource, RunLockScope, RunLockKey, logger, ct);
        if (runLock is null)
        {
            LogAnotherInstanceRunning(logger);
            return;
        }

        var interrupted = await store.FailInterruptedRunsAsync(time.GetUtcNow(), ct);
        if (interrupted > 0)
            LogInterruptedRuns(logger, interrupted);

        var registered = sources.ToList();
        var history = await store.LoadAsync(ct);

        var statuses = new List<SourceStatus>(registered.Count);
        foreach (var source in registered)
        {
            var known = history.GetValueOrDefault(source.Id);
            var probe = await ProbeAsync(source, ct);
            statuses.Add(new SourceStatus(source.Id, source.RefreshPolicy, probe, known?.Served, known?.LastAttemptFailed ?? false));
        }

        var plan = RefreshPlanner.Plan(statuses, time.GetUtcNow(), options.MaxRoutineImportsPerRun);
        LogPlan(logger, plan.Imports.Count, plan.Deferred.Count, plan.UpToDate.Count, plan.Unprobed.Count);
        foreach (var deferred in plan.Deferred)
            LogDeferred(logger, deferred.Source, deferred.Reason);

        // Confirmed before the imports, which may take hours: this is when it was true.
        await store.ConfirmCurrentAsync(plan.UpToDate, time.GetUtcNow(), ct);

        var sourcesById = registered.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var planned in plan.Imports)
            await ImportAsync(sourcesById[planned.Source], planned, ct);
    }

    /// <summary>The source's probe, or null if it failed; either way recorded on its state.</summary>
    private async Task<SourceProbe?> ProbeAsync(IPropertySource source, CancellationToken ct)
    {
        SourceProbe? probe = null;
        string? error = null;
        try
        {
            probe = await source.ProbeAsync(ct);
            LogProbed(logger, source.Id, probe.Fingerprint, probe.Kind);
        }
        // An HttpClient timeout is also an OperationCanceledException; only a cancelled run
        // may propagate, anything else is this source's failure.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogProbeFailed(logger, ex, source.Id);
            error = ex.Message;
        }

        await store.RecordProbeAsync(source.Id, time.GetUtcNow(), probe?.Fingerprint, error, ct);
        return probe;
    }

    private async Task ImportAsync(IPropertySource source, PlannedImport planned, CancellationToken ct)
    {
        var runId = await store.StartRunAsync(planned, time.GetUtcNow(), ct);
        var run = new ImportRunContext(runId, source.Id);
        LogImporting(logger, source.Id, planned.Reason, planned.Probe.Fingerprint);

        try
        {
            // Completes the run and serves it in the same transaction that swaps the rows in.
            var count = await writer.WriteAsync(run, source.FetchAsync(planned.Probe, run, ct), ct);
            LogImportComplete(logger, source.Id, count, run.SkippedParts);
        }
        // Someone else is importing this source right now; nothing was attempted here.
        catch (ImportAlreadyRunningException ex)
        {
            LogSourceLocked(logger, ex, source.Id);
            await store.DiscardRunAsync(runId, ct);
        }
        // A cancelled run is left unfinished here, like one cut short by a crash: the next run
        // records it as interrupted and plans the source again.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogImportFailed(logger, ex, source.Id);
            // Reported by the import health check until a later run of the source succeeds.
            await store.FailRunAsync(runId, time.GetUtcNow(), ex.Message, ct);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Another instance is running an import; skipping this run")]
    private static partial void LogAnotherInstanceRunning(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recorded {Count} unfinished import runs as failed: the process stopped during them")]
    private static partial void LogInterruptedRuns(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: another process is importing this source; skipping it this run")]
    private static partial void LogSourceLocked(ILogger logger, Exception exception, string source);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: probed, fingerprint {Fingerprint} ({Kind})")]
    private static partial void LogProbed(ILogger logger, string source, string fingerprint, FingerprintKind kind);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Source}: probe failed; skipping the source this run")]
    private static partial void LogProbeFailed(ILogger logger, Exception exception, string source);

    [LoggerMessage(Level = LogLevel.Information, Message = "Import plan: {Imports} to import, {Deferred} deferred to a later run, {UpToDate} up to date, {Unprobed} not probed")]
    private static partial void LogPlan(ILogger logger, int imports, int deferred, int upToDate, int unprobed);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: due ({Reason}), but deferred to a later run by the routine import cap")]
    private static partial void LogDeferred(ILogger logger, string source, ImportReason reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: importing ({Reason}), fingerprint {Fingerprint}...")]
    private static partial void LogImporting(ILogger logger, string source, ImportReason reason, string fingerprint);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: import complete, {Count} records, {SkippedParts} parts skipped")]
    private static partial void LogImportComplete(ILogger logger, string source, long count, int skippedParts);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Source}: import failed; the previous data stays and the source is due again next run")]
    private static partial void LogImportFailed(ILogger logger, Exception exception, string source);
}
