namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// Config for one INSPIRE-split Bundesland: a parcel WFS (area+geometry) and an address WFS
/// (text+geometry), joined spatially since neither dataset carries both. Adding a state is
/// adding an entry to "Import:Inspire:Sources" — no new code.
/// </summary>
public class InspireSourceOptions
{
    /// <summary>Stable slug for this source, e.g. "sh". Becomes <see cref="InspirePropertyImporter.Source"/>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// False stops importing this source and hides its already-imported rows from the app
    /// (they stay in the database). Keep the entry and flip this rather than deleting it —
    /// a deleted entry's rows would stay visible.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public string DatasetName { get; set; } = string.Empty;

    /// <summary>Base URL of the cp:CadastralParcel WFS (INSPIRE download service).</summary>
    public string ParcelWfsUrl { get; set; } = string.Empty;

    /// <summary>Base URL of the ad:Address WFS (INSPIRE download service).</summary>
    public string AddressWfsUrl { get; set; } = string.Empty;

    /// <summary>
    /// The srsName both WFS endpoints are expected to return geometry in (e.g.
    /// "http://www.opengis.net/def/crs/epsg/0/25832"). Parsed features whose srsName
    /// doesn't match this are rejected rather than silently joined in the wrong CRS.
    /// </summary>
    public string Crs { get; set; } = string.Empty;

    public InspireBoundingBox BoundingBox { get; set; } = new();

    /// <summary>Size (in the CRS's linear unit, i.e. metres for EPSG:25832) of each internal fetch tile.</summary>
    public double TileSizeMeters { get; set; } = 5000;

    /// <summary>Max features requested per GetFeature page; the importer pages via startIndex until exhausted.</summary>
    public int PageSize { get; set; } = 2000;
}

/// <summary>Statewide extent, in the CRS given by <see cref="InspireSourceOptions.Crs"/>, tiled internally for bounded memory use.</summary>
public class InspireBoundingBox
{
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
}
