using Grundstuecksfinder.Services.Importers.Scheduling;

namespace Grundstuecksfinder.Infrastructure;

/// <summary>
/// Runs the <see cref="ImportRunner"/> once at startup — so a fresh database is filled right
/// away instead of the next night — and then nightly at <see cref="NightlyRunAt"/> UTC.
/// </summary>
public sealed partial class ImportWorker(IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<ImportWorker> logger)
    : BackgroundService
{
    /// <summary>When the nightly run starts, in UTC: outside the day's traffic, here and at the state services.</summary>
    public static readonly TimeOnly NightlyRunAt = new(3, 0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunOnceAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeUntilNext(time.GetUtcNow(), NightlyRunAt);
            LogNextRunScheduled(logger, delay);
            await Task.Delay(delay, time, stoppingToken);
            await RunOnceAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Time from <paramref name="now"/> to the next <paramref name="at"/> (UTC), strictly in the
    /// future: a run that ends exactly at that time waits a full day, not zero.
    /// </summary>
    public static TimeSpan TimeUntilNext(DateTimeOffset now, TimeOnly at)
    {
        var utcNow = now.ToUniversalTime();
        var next = new DateTimeOffset(utcNow.Date, TimeSpan.Zero).Add(at.ToTimeSpan());
        if (next <= utcNow) next = next.AddDays(1);
        return next - utcNow;
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            // A fresh scope per run: the sources and the database context live for one run only.
            using var scope = scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<ImportRunner>();
            await runner.RunAsync(stoppingToken);
        }
        // An exception escaping a BackgroundService stops the whole host (the web app with it),
        // so a failed run (the database unreachable, say) is logged and the next one tried
        // tomorrow. Only shutdown's cancellation ends the loop.
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            LogRunFailed(logger, ex);
        }
    }

    // No hh\:mm\:ss format specifier: the logging source generator doesn't escape the
    // backslashes a TimeSpan custom format needs.
    [LoggerMessage(Level = LogLevel.Information, Message = "Next import run scheduled in {Delay}")]
    private static partial void LogNextRunScheduled(ILogger logger, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Import run failed; retrying at the next scheduled run")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
