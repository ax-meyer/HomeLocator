using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// Point-in-polygon lookup of a parcel's official area, built from one tile's worth of parcels.
/// The STRtree envelope query is only a cheap prefilter; containment is always re-checked exactly
/// against the polygon itself before a match is accepted.
/// </summary>
public sealed class ParcelSpatialIndex
{
    private readonly STRtree<(Polygon Polygon, double AreaM2)> _tree = new();

    public void Add(Polygon polygon, double areaM2) => _tree.Insert(polygon.EnvelopeInternal, (polygon, areaM2));

    public double? FindContainingParcelArea(Point location)
    {
        foreach (var candidate in _tree.Query(location.EnvelopeInternal))
        {
            try
            {
                if (candidate.Polygon.Contains(location))
                    return candidate.AreaM2;
            }
            catch (TopologyException)
            {
                // Some parcels have self-intersecting rings that NTS can't evaluate — skip them.
            }
        }
        return null;
    }
}
