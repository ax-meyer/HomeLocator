using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// Point-in-polygon lookup of a parcel's official area, built from one tile's worth of parcels.
/// The STRtree envelope query is only a cheap prefilter; containment is always re-checked exactly
/// against the geometry itself (a Polygon, or a MultiPolygon for multi-part parcels) before a
/// match is accepted.
/// </summary>
public sealed class ParcelSpatialIndex
{
    private readonly STRtree<(Geometry Geometry, double AreaM2)> _tree = new();

    public void Add(Geometry geometry, double areaM2) => _tree.Insert(geometry.EnvelopeInternal, (geometry, areaM2));

    /// <summary>
    /// Area of the parcel containing <paramref name="location"/>. Cadastral parcels shouldn't
    /// overlap; where faulty data makes them, the smallest containing parcel wins, so the result
    /// doesn't depend on the order the server returned them in.
    /// </summary>
    public double? FindContainingParcelArea(Point location)
    {
        double? best = null;
        var bestGeometryArea = double.MaxValue;
        foreach (var candidate in _tree.Query(location.EnvelopeInternal))
        {
            try
            {
                if (!candidate.Geometry.Contains(location)) continue;
            }
            catch (TopologyException)
            {
                // Some parcels have self-intersecting rings that NTS can't evaluate — skip them.
                continue;
            }

            var geometryArea = candidate.Geometry.Area;
            if (geometryArea < bestGeometryArea)
            {
                bestGeometryArea = geometryArea;
                best = candidate.AreaM2;
            }
        }
        return best;
    }
}
