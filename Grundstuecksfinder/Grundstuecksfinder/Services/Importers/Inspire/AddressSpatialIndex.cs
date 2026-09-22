using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// All of a source's addresses, held in memory and queryable by tile, for sources fetched in one
/// pass instead of per tile (see <see cref="InspireSourceOptions.PageAddressesWithStartIndex"/>).
/// Only worth it for small states: the whole address set stays resident for the import.
/// </summary>
public sealed class AddressSpatialIndex
{
    private readonly STRtree<AddressFeature> _tree;

    public AddressSpatialIndex(IReadOnlyList<AddressFeature> addresses)
    {
        Count = addresses.Count;
        // STRtree needs a positive node capacity even when empty.
        _tree = new STRtree<AddressFeature>(Math.Max(2, Math.Min(addresses.Count, 10)));
        foreach (var address in addresses)
            _tree.Insert(address.Location.EnvelopeInternal, address);
    }

    public int Count { get; }

    /// <summary>
    /// The addresses in an envelope. Like the WFS bbox filter this is closed on both edges; the
    /// caller still narrows it to the tile that owns each point.
    /// </summary>
    public IReadOnlyList<AddressFeature> Query(Envelope envelope) => [.. _tree.Query(envelope)];
}
