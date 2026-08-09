using System.Diagnostics.Metrics;
using Grundstuecksfinder.Data;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services;

namespace Grundstuecksfinder.Infrastructure;

/// <summary>
/// Hosted service that listens to OpenTelemetry-compatible metrics emitted by <see cref="AppMetrics"/>
/// via <see cref="MeterListener"/> and writes daily aggregates to the <c>DailyTelemetry</c> table.
/// Events are accumulated in memory and flushed to the database every minute as well as on shutdown.
/// </summary>
public sealed class TelemetryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelemetryWorker> _logger;

    // Atomic accumulators – incremented from any thread via MeterListener callback.
    private long _pageLoads;
    private long _searches;
    private long _tableRowOpens;
    private long _outgoingClicks;

    public TelemetryWorker(IServiceScopeFactory scopeFactory, ILogger<TelemetryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AppMetrics.MeterName)
                l.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>(OnMeasurement);
        listener.Start();

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await FlushAsync();
        }
        catch (OperationCanceledException) { }

        // Letzte Sicherung beim Herunterfahren
        await FlushAsync();
    }

    private void OnMeasurement(Instrument instrument, long measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        switch (instrument.Name)
        {
            case AppMetrics.PageLoadsName:
                Interlocked.Add(ref _pageLoads, measurement);
                break;
            case AppMetrics.SearchesName:
                Interlocked.Add(ref _searches, measurement);
                break;
            case AppMetrics.TableRowOpensName:
                Interlocked.Add(ref _tableRowOpens, measurement);
                break;
            case AppMetrics.OutgoingClicksName:
                Interlocked.Add(ref _outgoingClicks, measurement);
                break;
        }
    }

    private async Task FlushAsync()
    {
        // Atomisch austauschen, damit keine Ereignisse verloren gehen.
        var pageLoads      = Interlocked.Exchange(ref _pageLoads, 0);
        var searches       = Interlocked.Exchange(ref _searches, 0);
        var tableRowOpens  = Interlocked.Exchange(ref _tableRowOpens, 0);
        var outgoingClicks = Interlocked.Exchange(ref _outgoingClicks, 0);

        if (pageLoads == 0 && searches == 0 && tableRowOpens == 0 && outgoingClicks == 0)
            return;

        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var row = await db.DailyTelemetry.FindAsync(today);
            if (row is null)
            {
                db.DailyTelemetry.Add(new DailyTelemetry
                {
                    Date           = today,
                    PageLoads      = pageLoads,
                    Searches       = searches,
                    TableRowOpens  = tableRowOpens,
                    OutgoingClicks = outgoingClicks,
                });
            }
            else
            {
                row.PageLoads      += pageLoads;
                row.Searches       += searches;
                row.TableRowOpens  += tableRowOpens;
                row.OutgoingClicks += outgoingClicks;
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telemetrie-Flush in die Datenbank fehlgeschlagen");
        }
    }
}
