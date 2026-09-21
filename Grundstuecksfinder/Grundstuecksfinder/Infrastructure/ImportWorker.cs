using Grundstuecksfinder.Services.Importers;

namespace Grundstuecksfinder.Infrastructure;

/// <summary>
/// Runs every registered <see cref="IPropertyImporter"/> daily at 03:00 UTC.
/// </summary>
public partial class ImportWorker(IServiceScopeFactory scopeFactory, ILogger<ImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = scopeFactory.CreateScope())
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<ImportOrchestrator>();
            await orchestrator.CheckAndImportAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeUntilNext(new TimeOnly(3, 0));
            LogNextImportScheduled(logger, delay);
            await Task.Delay(delay, stoppingToken);

            using var scope = scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<ImportOrchestrator>();
            await orchestrator.CheckAndImportAsync(stoppingToken);
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
}
