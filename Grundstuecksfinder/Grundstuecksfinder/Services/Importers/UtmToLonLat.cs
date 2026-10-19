using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;

namespace Grundstuecksfinder.Services.Importers;

/// <summary>
/// Converts ETRS89/UTM coordinates (EPSG 25831–25833, the CRS every INSPIRE source is read in)
/// to WGS84 lon/lat.
/// </summary>
/// <remarks>
/// ETRS89 and WGS84 differ by well under a metre, far below what matters for a PLZ area or a map
/// pin, so ETRS89/UTM is treated as WGS84/UTM. Not thread-safe: one instance per import.
/// </remarks>
public sealed class UtmToLonLat
{
    private readonly MathTransform _transform;

    /// <param name="utmEpsg">25831, 25832 or 25833.</param>
    public UtmToLonLat(int utmEpsg)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(utmEpsg, 25831);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(utmEpsg, 25833);

        var utm = ProjectedCoordinateSystem.WGS84_UTM(utmEpsg - 25800, zoneIsNorth: true);
        _transform = new CoordinateTransformationFactory()
            .CreateFromCoordinateSystems(utm, GeographicCoordinateSystem.WGS84).MathTransform;
    }

    public (double Lon, double Lat) Transform(double x, double y) => _transform.Transform(x, y);
}
