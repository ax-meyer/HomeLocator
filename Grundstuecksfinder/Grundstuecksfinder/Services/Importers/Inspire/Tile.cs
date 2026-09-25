using NetTopologySuite.Geometries;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>A fetch tile; bounds in the source's CRS.</summary>
public readonly record struct Tile(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>The bounds as a WFS bbox parameter spells them (without the CRS).</summary>
    public string Bbox => FormattableString.Invariant($"{MinX},{MinY},{MaxX},{MaxY}");

    public Envelope Envelope => new(MinX, MaxX, MinY, MaxY);

    /// <summary>Half-open, so each point belongs to exactly one of two adjacent tiles.</summary>
    public bool Owns(Point p) => p.X >= MinX && p.X < MaxX && p.Y >= MinY && p.Y < MaxY;

    /// <summary>Closed, like the WFS bbox filter: what a request for this tile would return.</summary>
    public bool Intersects(Envelope e) => e.MinX <= MaxX && e.MaxX >= MinX && e.MinY <= MaxY && e.MaxY >= MinY;

    /// <summary>The longer side, in metres.</summary>
    public double Size => Math.Max(MaxX - MinX, MaxY - MinY);

    /// <summary>The four quarters, in the order they should be pushed onto a stack.</summary>
    public Tile[] Quarters()
    {
        var midX = (MinX + MaxX) / 2;
        var midY = (MinY + MaxY) / 2;
        // Pushed onto a stack, so the reverse of the order they'll be processed in.
        return
        [
            new Tile(midX, midY, MaxX, MaxY),
            new Tile(MinX, midY, midX, MaxY),
            new Tile(midX, MinY, MaxX, midY),
            new Tile(MinX, MinY, midX, midY),
        ];
    }

    /// <summary>The initial tiling of a bounding box, column by column.</summary>
    public static IEnumerable<Tile> Grid(InspireBoundingBox bbox, double size)
    {
        for (var x = bbox.MinX; x < bbox.MaxX; x += size)
        {
            for (var y = bbox.MinY; y < bbox.MaxY; y += size)
                yield return new Tile(x, y, Math.Min(x + size, bbox.MaxX), Math.Min(y + size, bbox.MaxY));
        }
    }
}
