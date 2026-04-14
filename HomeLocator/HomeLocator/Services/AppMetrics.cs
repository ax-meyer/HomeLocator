using Prometheus;

namespace HomeLocator.Services;

public sealed class AppMetrics
{
    private readonly Counter _pageLoads;
    private readonly Counter _searches;
    private readonly Counter _mapClicks;

    public AppMetrics()
    {
        _pageLoads = Metrics.CreateCounter(
            "homelocator_page_loads_total", "Homepage visits (interactive sessions)");
        _searches  = Metrics.CreateCounter(
            "homelocator_searches_total", "Property search queries executed");
        _mapClicks = Metrics.CreateCounter(
            "homelocator_map_clicks_total", "External map link clicks",
            new CounterConfiguration { LabelNames = ["map"] });
    }

    public void RecordPageLoad()               => _pageLoads.Inc();
    public void RecordSearch()                 => _searches.Inc();
    public void RecordMapClick(string mapName) => _mapClicks.WithLabels(mapName).Inc();
}
