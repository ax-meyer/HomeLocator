namespace Grundstuecksfinder.Models;

public class Property
{
    public int Id { get; set; }
    public string? Str { get; set; }
    public string? Hnr { get; set; }
    public string? HnrZus { get; set; }
    public string? Plz { get; set; }
    public string? Ort { get; set; }
    public string? Gemeinde { get; set; }
    public double? FlaecheAmtl { get; set; }

    // WGS84 position of the address from the source itself (the official house coordinate), so
    // the map pin needs no geocoding; null where the source has none (NRW). real, not double:
    // ~0.5 m is plenty for a pin and halves the size over millions of rows.
    public float? Lat { get; set; }
    public float? Lon { get; set; }
    public string Source { get; set; } = string.Empty;
    public int ImportRunId { get; set; }
    public ImportRun ImportRun { get; set; } = null!;
}
