using Grundstuecksfinder.Services.Importers;

namespace Grundstuecksfinder.Infrastructure;

/// <summary>
/// Runs every registered <see cref="IPropertyImporter"/> daily at 03:00 UTC.
/// </summary>
public partial class ImportWorker(IServiceScopeFactory scopeFactory, ILogger<ImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunOnceAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeUntilNext(new TimeOnly(3, 0));
            LogNextImportScheduled(logger, delay);
            await Task.Delay(delay, stoppingToken);
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<ImportOrchestrator>();
            await orchestrator.CheckAndImportAsync(stoppingToken);
        }
        // An exception escaping a BackgroundService stops the whole host (the web app with it),
        // so a failed run is logged and the next one tried tomorrow.
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            LogRunFailed(logger, ex);
        }
    }

    private static TimeSpan TimeUntilNext(TimeOnly target)
    {
        var now = DateTimeOffset.UtcNow;
        var next = new DateTimeOffset(now.Date, TimeSpan.Zero).Add(target.ToTimeSpan());
        if (next <= now) next = next.AddDays(1);
        return next - now;
    }

    // No hh\:mm\:ss format specifier: the logging source generator doesn't escape the
    // backslashes a TimeSpan custom format needs.
    [LoggerMessage(Level = LogLevel.Information, Message = "Next import scheduled in {Delay}")]
    private static partial void LogNextImportScheduled(ILogger logger, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Import run failed; retrying at the next scheduled run")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
