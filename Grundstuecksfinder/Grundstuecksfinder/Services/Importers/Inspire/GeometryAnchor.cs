using NetTopologySuite.Geometries;

namespace Grundstuecksfinder.Services.Importers.Inspire;

internal static class GeometryAnchor
{
    /// <summary>
    /// The point that stands for a parcel or building: inside it (so the tile owning the point
    /// is one the geometry touches, and its postcode area is the geometry's own), and the same
    /// in every tile's response. A geometry too broken for that falls back to its first vertex;
    /// an empty one is null.
    /// </summary>
    public static Point? Of(Geometry geometry)
    {
        if (geometry.IsEmpty) return null;
        try
        {
            var point = geometry.InteriorPoint;
            if (point is { IsEmpty: false }) return point;
        }
        catch (TopologyException)
        {
        }
        return geometry.Factory.CreatePoint(geometry.Coordinates[0]);
    }
}
