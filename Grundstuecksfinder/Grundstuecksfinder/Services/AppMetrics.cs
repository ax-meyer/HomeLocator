using System.Diagnostics.Metrics;

namespace Grundstuecksfinder.Services;

/// <summary>
/// Thin instrumentation layer built on System.Diagnostics.Metrics (OpenTelemetry-compatible).
/// The <see cref="Infrastructure.TelemetryWorker"/> subscribes via <see cref="MeterListener"/> and persists
/// aggregated daily counts to the database.
/// </summary>
public sealed class AppMetrics : IDisposable
{
    public const string MeterName = "Grundstuecksfinder";
    public const string PageLoadsName = "grundstuecksfinder.page_loads";
    public const string SearchesName = "grundstuecksfinder.searches";
    public const string TableRowOpensName = "grundstuecksfinder.table_row_opens";
    public const string OutgoingClicksName = "grundstuecksfinder.outgoing_clicks";

    private readonly Meter _meter;
    private readonly Counter<long> _pageLoads;
    private readonly Counter<long> _searches;
    private readonly Counter<long> _tableRowOpens;
    private readonly Counter<long> _outgoingClicks;

    public AppMetrics()
    {
        _meter = new Meter(MeterName, "1.0");
        _pageLoads      = _meter.CreateCounter<long>(PageLoadsName,      description: "Homepage-Aufrufe (interaktive Sessions)");
        _searches       = _meter.CreateCounter<long>(SearchesName,       description: "Ausgeführte Suchanfragen");
        _tableRowOpens  = _meter.CreateCounter<long>(TableRowOpensName,  description: "Geöffnete Tabelleneinträge");
        _outgoingClicks = _meter.CreateCounter<long>(OutgoingClicksName, description: "Klicks auf ausgehende Links");
    }

    public void RecordPageLoad()                    => _pageLoads.Add(1);
    public void RecordSearch()                      => _searches.Add(1);
    public void RecordTableRowOpen()                => _tableRowOpens.Add(1);
    public void RecordOutgoingClick(string button)  => _outgoingClicks.Add(1, new KeyValuePair<string, object?>("button", button));

    public void Dispose() => _meter.Dispose();
}
