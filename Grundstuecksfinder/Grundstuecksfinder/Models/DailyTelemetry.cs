namespace Grundstuecksfinder.Models;

public class DailyTelemetry
{
    public DateOnly Date { get; set; }
    public long PageLoads { get; set; }
    public long Searches { get; set; }
    public long TableRowOpens { get; set; }
    public long OutgoingClicks { get; set; }
}
