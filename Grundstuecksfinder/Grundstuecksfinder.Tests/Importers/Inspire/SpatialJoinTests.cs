using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using NetTopologySuite.Geometries;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

/// <summary>
/// Exercises the point-in-polygon join in isolation (no HTTP, no WFS) — synthetic squares and
/// points chosen to cover containment, misses, and shared-edge ambiguity.
/// </summary>
public sealed class SpatialJoinTests
{
    private static readonly GeometryFactory Factory = new();

    private static Polygon Square(double minX, double minY, double size) => Factory.CreatePolygon([
        new Coordinate(minX, minY),
        new Coordinate(minX + size, minY),
        new Coordinate(minX + size, minY + size),
        new Coordinate(minX, minY + size),
        new Coordinate(minX, minY),
    ]);

    private static Point PointAt(double x, double y) => Factory.CreatePoint(new Coordinate(x, y));

    [Fact]
    public void FindContainingParcelArea_PointInsideParcel_ReturnsThatParcelsArea()
    {
        var index = new ParcelSpatialIndex();
        index.Add(Square(0, 0, 10), 100);
        index.Add(Square(20, 0, 10), 200);

        index.FindContainingParcelArea(PointAt(5, 5)).Should().Be(100);
        index.FindContainingParcelArea(PointAt(25, 5)).Should().Be(200);
    }

    [Fact]
    public void FindContainingParcelArea_PointOutsideEveryParcel_ReturnsNull()
    {
        var index = new ParcelSpatialIndex();
        index.Add(Square(0, 0, 10), 100);
        index.Add(Square(20, 0, 10), 200);

        index.FindContainingParcelArea(PointAt(15, 5)).Should().BeNull("the gap between the two squares belongs to no parcel");
    }

    [Fact]
    public void FindContainingParcelArea_PointOnSharedEdge_PicksTheContainingPolygon()
    {
        var index = new ParcelSpatialIndex();
        index.Add(Square(0, 0, 10), 100);
        index.Add(Square(10, 0, 10), 200); // shares the x=10 edge with the first square

        // Envelopes of both squares overlap at x=10, so the STRtree prefilter returns both
        // candidates — exact Contains() must still pick the one that's geometrically correct.
        index.FindContainingParcelArea(PointAt(5, 5)).Should().Be(100);
        index.FindContainingParcelArea(PointAt(15, 5)).Should().Be(200);
    }

    [Fact]
    public void FindContainingParcelArea_EmptyIndex_ReturnsNull()
    {
        var index = new ParcelSpatialIndex();

        index.FindContainingParcelArea(PointAt(0, 0)).Should().BeNull();
    }

    [Fact]
    public void FindContainingParcelArea_MultiPartParcel_MatchesAddressInAnyPart()
    {
        var index = new ParcelSpatialIndex();
        index.Add(Factory.CreateMultiPolygon([Square(0, 0, 10), Square(50, 0, 10)]), 300);

        index.FindContainingParcelArea(PointAt(55, 5)).Should().Be(300, "the address lies in the parcel's second part");
    }

    [Fact]
    public void FindContainingParcelArea_OverlappingParcels_SmallestContainingParcelWins()
    {
        // Faulty cadastral data can overlap; the result must not depend on insertion order.
        var big = Square(0, 0, 100);
        var small = Square(40, 40, 10);

        var bigFirst = new ParcelSpatialIndex();
        bigFirst.Add(big, 10_000);
        bigFirst.Add(small, 100);
        var smallFirst = new ParcelSpatialIndex();
        smallFirst.Add(small, 100);
        smallFirst.Add(big, 10_000);

        bigFirst.FindContainingParcelArea(PointAt(45, 45)).Should().Be(100);
        smallFirst.FindContainingParcelArea(PointAt(45, 45)).Should().Be(100);
    }
}
