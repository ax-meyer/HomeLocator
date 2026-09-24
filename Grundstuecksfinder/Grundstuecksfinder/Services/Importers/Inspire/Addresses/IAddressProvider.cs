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
    /// per-tile request needs. Called once per import, before the first tile, with the part
    /// <see cref="ProbeAsync"/> returned in the run being imported: a provider whose upstream
    /// can change between probe and fetch (hours apart, across midnight on an initial run)
    /// checks what it finds against it.
    /// </summary>
    Task<ITileAddresses> LoadAsync(FingerprintPart probed, CancellationToken ct);
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
    /// The version of the addresses actually loaded, for a provider whose upstream can move
    /// between probe and fetch (a file located again at download time); null where the probe's
    /// part stands for what is fetched.
    /// </summary>
    FingerprintPart? Loaded => null;

    /// <summary>
    /// The addresses in a tile, including those on its edges (the caller keeps only the ones the
    /// tile owns), or <see cref="AddressTile.Full"/> when there are more than one request can
    /// hold and the tile must be split.
    /// </summary>
    Task<AddressTile> GetAsync(Tile tile, CancellationToken ct);
}

/// <summary>What <see cref="ITileAddresses.GetAsync"/> found in one tile.</summary>
public readonly record struct AddressTile(IReadOnlyList<AddressFeature> Features, bool IsFull)
{
    public static AddressTile Full => new([], IsFull: true);

    public static AddressTile Of(IReadOnlyList<AddressFeature> features) => new(features, IsFull: false);
}
