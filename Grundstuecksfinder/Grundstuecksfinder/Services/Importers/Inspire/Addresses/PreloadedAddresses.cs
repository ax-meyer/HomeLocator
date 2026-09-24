using System.Globalization;
using System.Runtime.InteropServices;
using NetTopologySuite.Geometries;

namespace Grundstuecksfinder.Services.Importers.Inspire.Addresses;

/// <summary>
/// All of a source's addresses, held in memory for the whole import and queried tile by tile,
/// for address sources that come in one piece rather than per tile (a paged service, a file).
/// </summary>
/// <remarks>
/// <para>
/// Sized for the largest state: Baden-Württemberg's ~3.1 M addresses. One object per address
/// (a record, an NTS Point, an STRtree node) costs a few hundred bytes each — around a gigabyte
/// for BW — so an address is kept as a 32-byte struct instead: its coordinates and three
/// indices into tables of distinct values. Street names, house numbers and (Plz, Ort, Gemeinde)
/// combinations repeat heavily (BW: ~3.1 M addresses, but only tens of thousands of distinct
/// streets and places), so each distinct value is stored once.
/// </para>
/// <para>
/// The spatial index is a uniform grid: the rows are sorted by grid cell, and each cell maps to
/// its run of rows. Tiles are axis-aligned rectangles, so a query visits the cells a tile covers
/// and filters their points — no per-address index nodes at all.
/// </para>
/// </remarks>
public sealed class PreloadedAddresses : ITileAddresses
{
    private static readonly GeometryFactory GeometryFactory = new();

    private readonly List<Row> _rows;
    private readonly string[] _streets;
    private readonly (string? Hnr, string? HnrZus)[] _houseNumbers;
    private readonly (string? Plz, string? Ort, string? Gemeinde)[] _places;
    private readonly Dictionary<(int X, int Y), (int Start, int Count)> _cells;
    private readonly double _cellSize;

    /// <summary>
    /// The range of cells holding any address, so a query reaching far beyond the data (the
    /// whole state's bbox, say) doesn't walk millions of empty cells.
    /// </summary>
    private readonly (int MinX, int MinY, int MaxX, int MaxY) _occupied;

    private PreloadedAddresses(Builder builder, long expectedCount, FingerprintPart? loaded)
    {
        Loaded = loaded;
        _rows = builder.Rows;
        _cellSize = builder.CellSize;
        _streets = [.. builder.Streets.Values];
        _houseNumbers = [.. builder.HouseNumbers.Values];
        _places = [.. builder.Places.Values];
        ExpectedCount = expectedCount;

        var cellSize = _cellSize;
        CollectionsMarshal.AsSpan(_rows).Sort((a, b) =>
        {
            var byX = Cell(a.X, cellSize).CompareTo(Cell(b.X, cellSize));
            return byX != 0 ? byX : Cell(a.Y, cellSize).CompareTo(Cell(b.Y, cellSize));
        });

        _cells = [];
        _occupied = (int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);
        var rows = CollectionsMarshal.AsSpan(_rows);
        for (var start = 0; start < rows.Length;)
        {
            var key = (X: Cell(rows[start].X, cellSize), Y: Cell(rows[start].Y, cellSize));
            var end = start + 1;
            while (end < rows.Length && (Cell(rows[end].X, cellSize), Cell(rows[end].Y, cellSize)) == key) end++;
            _cells[key] = (start, end - start);
            _occupied = (Math.Min(_occupied.MinX, key.X), Math.Min(_occupied.MinY, key.Y),
                Math.Max(_occupied.MaxX, key.X), Math.Max(_occupied.MaxY, key.Y));
            start = end;
        }
    }

    /// <summary>Addresses held.</summary>
    public int Count => _rows.Count;

    public long ExpectedCount { get; }

    public FingerprintPart? Loaded { get; }

    public string Description => string.Create(CultureInfo.InvariantCulture, $"{Count} preloaded");

    public Task<AddressTile> GetAsync(Tile tile, CancellationToken ct) =>
        // Already in memory, so a tile is never too full for its addresses.
        Task.FromResult(AddressTile.Of(Query(tile.Envelope)));

    /// <summary>
    /// The addresses in an envelope. Like the WFS bbox filter this is closed on every edge; the
    /// caller still narrows it to the tile that owns each point.
    /// </summary>
    public IReadOnlyList<AddressFeature> Query(Envelope envelope)
    {
        var result = new List<AddressFeature>();
        var rows = CollectionsMarshal.AsSpan(_rows);
        var maxX = Math.Min(Cell(envelope.MaxX, _cellSize), _occupied.MaxX);
        var maxY = Math.Min(Cell(envelope.MaxY, _cellSize), _occupied.MaxY);
        for (var cx = Math.Max(Cell(envelope.MinX, _cellSize), _occupied.MinX); cx <= maxX; cx++)
        {
            for (var cy = Math.Max(Cell(envelope.MinY, _cellSize), _occupied.MinY); cy <= maxY; cy++)
            {
                if (!_cells.TryGetValue((cx, cy), out var cell)) continue;
                foreach (ref readonly var row in rows.Slice(cell.Start, cell.Count))
                {
                    if (row.X < envelope.MinX || row.X > envelope.MaxX || row.Y < envelope.MinY || row.Y > envelope.MaxY)
                        continue;
                    result.Add(Materialize(row));
                }
            }
        }
        return result;
    }

    private AddressFeature Materialize(in Row row)
    {
        var (hnr, hnrZus) = _houseNumbers[row.HouseNumber];
        var (plz, ort, gemeinde) = _places[row.Place];
        return new AddressFeature(
            GeometryFactory.CreatePoint(new Coordinate(row.X, row.Y)),
            row.Street < 0 ? null : _streets[row.Street], hnr, hnrZus, plz, ort, gemeinde);
    }

    private static int Cell(double coordinate, double cellSize) =>
        (int)Math.Clamp(Math.Floor(coordinate / cellSize), int.MinValue, int.MaxValue);

    /// <summary>One address: 8+8+4+4+4 bytes, 32 with padding.</summary>
    internal readonly record struct Row(double X, double Y, int Street, int HouseNumber, int Place);

    /// <summary>
    /// Collects addresses one at a time, storing every distinct value once. Not thread-safe;
    /// <see cref="Build"/> hands its storage over to the index, so a builder is used only once.
    /// </summary>
    /// <param name="cellSizeMeters">
    /// Grid cell edge. Tiles run from a few kilometres down to tens of metres after splitting; a
    /// few hundred metres keeps both the cells a big tile visits and the points a small tile
    /// filters in check.
    /// </param>
    public sealed class Builder(double cellSizeMeters = 500)
    {
        internal List<Row> Rows { get; } = [];
        internal double CellSize => cellSizeMeters;
        internal InternTable<string> Streets { get; } = new();
        internal InternTable<(string?, string?)> HouseNumbers { get; } = new();
        internal InternTable<(string?, string?, string?)> Places { get; } = new();

        public int Count => Rows.Count;

        public void Add(AddressFeature address) =>
            Add(address.Location.X, address.Location.Y, address.Str, address.Hnr, address.HnrZus,
                address.Plz, address.Ort, address.Gemeinde);

        public void Add(double x, double y, string? str, string? hnr, string? hnrZus, string? plz, string? ort, string? gemeinde) =>
            Rows.Add(new Row(x, y,
                str is null ? -1 : Streets.Intern(str),
                HouseNumbers.Intern((hnr, hnrZus)),
                Places.Intern((plz, ort, gemeinde))));

        /// <param name="expectedCount">What the source says it has; see <see cref="ITileAddresses.ExpectedCount"/>.</param>
        /// <param name="loaded">The version loaded, if it may differ from the probe's; see <see cref="ITileAddresses.Loaded"/>.</param>
        public PreloadedAddresses Build(long expectedCount, FingerprintPart? loaded = null) => new(this, expectedCount, loaded);
    }

    /// <summary>Distinct values in the order first seen, each with its index.</summary>
    internal sealed class InternTable<T> where T : notnull
    {
        private readonly Dictionary<T, int> _indices = [];
        private readonly List<T> _values = [];

        public IReadOnlyList<T> Values => _values;

        public int Intern(T value)
        {
            ref var index = ref CollectionsMarshal.GetValueRefOrAddDefault(_indices, value, out var exists);
            if (!exists)
            {
                index = _values.Count;
                _values.Add(value);
            }
            return index;
        }
    }
}
