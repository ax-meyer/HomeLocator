namespace Grundstuecksfinder.Services.Importers.Inspire.Addresses;

/// <summary>
/// The address side of a source (text + point), joined to the INSPIRE parcels by
/// <see cref="ParcelAddressJoin"/>. Which implementation a source uses is its
/// <see cref="AddressSourceOptions.Type"/>: a state's INSPIRE ad:Address WFS where that is
/// usable, otherwise whatever faster or cleaner address dataset the state publishes.
/// </summary>
public interface IAddressProvider
{
    /// <summary>
    /// This side's part of the source's probe, from a cheap upstream check (a hit count, a HEAD
    /// request) that downloads no data. Throws when there is nothing importable; the source is
    /// then skipped this run.
    /// </summary>
    Task<FingerprintPart> ProbeAsync(CancellationToken ct);

    /// <summary>
    /// Readies the addresses for the tile loop — preloads all of them, or only learns what a
    /// per-tile request needs. Called once per import, before the first tile.
    /// </summary>
    Task<ITileAddresses> LoadAsync(CancellationToken ct);
}

/// <summary>A source's addresses, as the tile loop asks for them.</summary>
public interface ITileAddresses
{
    /// <summary>
    /// How many addresses the import should end up with, as the source itself counts them: the
    /// completeness check measures the joined addresses against this.
    /// </summary>
    long ExpectedCount { get; }

    /// <summary>How the addresses are fetched, for the import log.</summary>
    string Description { get; }

    /// <summary>
    /// The addresses in a tile, including those on its edges (the caller keeps only the ones the
    /// tile owns), or <see cref="AddressTile.Full"/> when there are more than one request can
    /// hold and the tile must be split.
    /// </summary>
    Task<AddressTile> GetAsync(Tile tile, CancellationToken ct);

    /// <summary>Logs whatever the provider counted along the way, once the tile loop is done.</summary>
    void ReportSummary() { }
}

/// <summary>What <see cref="ITileAddresses.GetAsync"/> found in one tile.</summary>
public readonly record struct AddressTile(IReadOnlyList<AddressFeature> Features, bool IsFull)
{
    public static AddressTile Full => new([], IsFull: true);

    public static AddressTile Of(IReadOnlyList<AddressFeature> features) => new(features, IsFull: false);
}
