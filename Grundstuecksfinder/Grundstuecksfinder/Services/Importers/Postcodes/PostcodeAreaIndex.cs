using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.IO;
using Newtonsoft.Json;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;

namespace Grundstuecksfinder.Services.Importers.Postcodes;

/// <summary>Finds the postcode (PLZ) whose area contains a point in a source's CRS.</summary>
public interface IPostcodeLookup
{
    string? FindPostcode(Point location);
}

/// <summary>
/// Point-in-polygon lookup against postcode areas (GeoJSON in WGS84 lon/lat, one feature per
/// area with a "postcode" property), for sources that publish no PLZ. Only the areas touching
/// the source's bounding box are kept: the full German file is ~500 MB of JSON, so it's read as
/// a stream, one feature at a time.
/// </summary>
public sealed class PostcodeAreaIndex : IPostcodeLookup
{
    private readonly STRtree<(IPreparedGeometry Area, double Size, string Postcode)> _tree = new();
    private readonly MathTransform _toLonLat;

    private PostcodeAreaIndex(int utmEpsg)
    {
        // ETRS89 and WGS84 differ by well under a metre, far below what matters for a PLZ area.
        var utm = ProjectedCoordinateSystem.WGS84_UTM(utmEpsg - 25800, zoneIsNorth: true);
        _toLonLat = new CoordinateTransformationFactory()
            .CreateFromCoordinateSystems(utm, GeographicCoordinateSystem.WGS84).MathTransform;
    }

    /// <summary>Number of postcode areas kept.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Reads postcode areas from <paramref name="geoJson"/>, keeping those that intersect
    /// <paramref name="bbox"/> (in the ETRS89/UTM CRS <paramref name="utmEpsg"/>, 25831–25833).
    /// Postcodes that aren't five digits (foreign areas along the border) are skipped.
    /// </summary>
    public static PostcodeAreaIndex Load(Stream geoJson, int utmEpsg, Envelope bbox)
    {
        var index = new PostcodeAreaIndex(utmEpsg);
        var filter = index.ToLonLat(bbox);
        var serializer = GeoJsonSerializer.Create();

        using var reader = new JsonTextReader(new StreamReader(geoJson));
        // Top-level "features" array: the reader never holds more than one feature.
        while (reader.Read())
        {
            if (reader is not { TokenType: JsonToken.PropertyName, Depth: 1 } || (string?)reader.Value != "features")
                continue;

            reader.Read();
            while (reader.Read() && reader.TokenType == JsonToken.StartObject)
            {
                var feature = serializer.Deserialize<IFeature>(reader);
                if (feature?.Geometry is not (Polygon or MultiPolygon) || !feature.Geometry.EnvelopeInternal.Intersects(filter))
                    continue;
                var postcode = PostalCode.Normalize(feature.Attributes?.GetOptionalValue("postcode") as string);
                if (postcode is null)
                    continue;

                index._tree.Insert(feature.Geometry.EnvelopeInternal,
                    (PreparedGeometryFactory.Prepare(feature.Geometry), feature.Geometry.Area, postcode));
                index.Count++;
            }
            break;
        }

        index._tree.Build();
        return index;
    }

    /// <summary>
    /// The postcode of the area containing <paramref name="location"/> (in the CRS given to
    /// <see cref="Load"/>), or null in the rare gaps between areas. Where OSM areas overlap, the
    /// smallest one wins, so the result doesn't depend on the file's order.
    /// </summary>
    public string? FindPostcode(Point location)
    {
        var (lon, lat) = _toLonLat.Transform(location.X, location.Y);
        var point = new Point(lon, lat);

        string? best = null;
        var bestSize = double.MaxValue;
        foreach (var candidate in _tree.Query(point.EnvelopeInternal))
        {
            if (candidate.Size < bestSize && candidate.Area.Contains(point))
            {
                best = candidate.Postcode;
                bestSize = candidate.Size;
            }
        }
        return best;
    }

    /// <summary>Envelope of <paramref name="utm"/> in lon/lat, from its corners.</summary>
    private Envelope ToLonLat(Envelope utm)
    {
        var result = new Envelope();
        foreach (var (x, y) in new[] { (utm.MinX, utm.MinY), (utm.MinX, utm.MaxY), (utm.MaxX, utm.MinY), (utm.MaxX, utm.MaxY) })
        {
            var (lon, lat) = _toLonLat.Transform(x, y);
            result.ExpandToInclude(lon, lat);
        }
        // UTM grid lines are curved in lon/lat: a margin keeps areas at the bbox's edge.
        result.ExpandBy(0.1);
        return result;
    }
}
