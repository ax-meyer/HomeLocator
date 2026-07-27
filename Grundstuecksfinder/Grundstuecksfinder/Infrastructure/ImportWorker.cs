using Grundstuecksfinder.Services;

namespace Grundstuecksfinder.Infrastructure;

/// <summary>
/// Runs the NRW data import daily at 03:00 UTC.
/// </summary>
public class ImportWorker(IServiceScopeFactory scopeFactory, ILogger<ImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = scopeFactory.CreateScope())
        {
            var importService = scope.ServiceProvider.GetRequiredService<DataImportService>();
            await importService.CheckAndImportAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeUntilNext(new TimeOnly(3, 0));
            logger.LogInformation(@"Next import scheduled in {Delay:hh\:mm\:ss}", delay);
            await Task.Delay(delay, stoppingToken);

            using var scope = scopeFactory.CreateScope();
            var importService = scope.ServiceProvider.GetRequiredService<DataImportService>();
            await importService.CheckAndImportAsync(stoppingToken);
        }
    }

    private static TimeSpan TimeUntilNext(TimeOnly target)
    {
        var now = DateTimeOffset.UtcNow;
        var next = new DateTimeOffset(now.Date, TimeSpan.Zero).Add(target.ToTimeSpan());
        if (next <= now) next = next.AddDays(1);
        return next - now;
    }
}
