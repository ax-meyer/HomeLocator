using NetTopologySuite.Geometries;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// The buildings around some parcels, by the addresses they name: where a parcel names several
/// addresses, its own interior point would mark none of them, but the cadastre ties each address
/// to a building, and that building is where the address is.
/// </summary>
/// <remarks>
/// The building is not always on the parcel naming the address: a terrace or a block can stand
/// over several parcels, and its address is then named by the neighbouring parcel too. So the
/// nearest building naming the address counts, up to <see cref="MaxDistanceMeters"/> away; the
/// same street and number elsewhere in the area (another Ortsteil, a renamed street) is further.
/// </remarks>
public sealed class BuildingPositions
{
    /// <summary>
    /// How far a building may be from the parcel naming its address. In Mainz's old town, the
    /// buildings that stand on a neighbouring parcel were 7–35 m away.
    /// </summary>
    public const double MaxDistanceMeters = 100;

    private readonly Dictionary<AddressKey, List<Geometry>> _byAddress = [];

    public BuildingPositions(IEnumerable<BuildingFeature> buildings)
    {
        foreach (var building in buildings)
        {
            foreach (var address in LagebezeichnungParser.Parse(building.Lagebezeichnung).Addresses)
            {
                var key = AddressKey.Of(address);
                if (!_byAddress.TryGetValue(key, out var outlines))
                    _byAddress[key] = outlines = [];
                outlines.Add(building.Geometry);
            }
        }
    }

    /// <summary>Addresses some building names.</summary>
    public int Addresses => _byAddress.Count;

    /// <summary>
    /// Inside the building naming <paramref name="address"/> that is nearest to
    /// <paramref name="parcel"/>, the larger one if several touch it; null if none is within
    /// <see cref="MaxDistanceMeters"/>.
    /// </summary>
    public Point? Find(LagebezeichnungAddress address, Geometry parcel)
    {
        if (!_byAddress.TryGetValue(AddressKey.Of(address), out var outlines)) return null;

        Geometry? best = null;
        var bestDistance = double.MaxValue;
        foreach (var outline in outlines)
        {
            var distance = parcel.Distance(outline);
            if (distance > MaxDistanceMeters) continue;
            if (distance < bestDistance || (distance == bestDistance && outline.Area > best!.Area))
            {
                best = outline;
                bestDistance = distance;
            }
        }
        return best is null ? null : GeometryAnchor.Of(best);
    }

    /// <summary>
    /// An address as both sides spell it. The suffix is compared in upper case: Thüringen writes
    /// it in lower case ("34d"), Rheinland-Pfalz in upper ("9 A"), and nothing promises that a
    /// parcel and its building agree.
    /// </summary>
    private readonly record struct AddressKey(string Street, string Number, string Suffix)
    {
        public static AddressKey Of(LagebezeichnungAddress a) =>
            new(a.Str, a.Hnr, a.HnrZus?.ToUpperInvariant() ?? "");
    }
}
