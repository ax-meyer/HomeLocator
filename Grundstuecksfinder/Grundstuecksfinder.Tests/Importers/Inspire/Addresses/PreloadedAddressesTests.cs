using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Grundstuecksfinder.Services.Importers.Inspire.Addresses;
using NetTopologySuite.Geometries;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire.Addresses;

public sealed class PreloadedAddressesTests
{
    private static PreloadedAddresses Build(double cellSize, params (double X, double Y, string Str)[] points)
    {
        var builder = new PreloadedAddresses.Builder(cellSize);
        foreach (var (x, y, str) in points)
            builder.Add(x, y, str, "1", null, null, "Ort", "Gemeinde");
        return builder.Build(expectedCount: points.Length);
    }

    [Fact]
    public void Query_ReturnsThePointsInTheEnvelopeAcrossCells()
    {
        var index = Build(10,
            (5, 5, "inside, first cell"),
            (25, 15, "inside, another cell"),
            (35, 5, "outside"),
            (5, 45, "outside too"));

        index.Query(new Envelope(0, 30, 0, 20)).Select(a => a.Str)
            .Should().BeEquivalentTo("inside, first cell", "inside, another cell");
    }

    [Fact]
    public void Query_IsClosedOnEveryEdge_LikeTheWfsBboxFilter()
    {
        // The join keeps only the points a tile owns (half-open); the query must not lose the
        // ones on the far edges before it gets the chance, even where an edge is a cell border.
        var index = Build(10, (0, 0, "min corner"), (20, 20, "max corner"), (20, 5, "right edge"), (20.001, 5, "beyond"));

        index.Query(new Envelope(0, 20, 0, 20)).Select(a => a.Str)
            .Should().BeEquivalentTo("min corner", "max corner", "right edge");
    }

    [Fact]
    public void Query_NegativeCoordinates_AreFound()
    {
        var index = Build(10, (-5, -5, "negative"), (5, 5, "positive"));

        index.Query(new Envelope(-10, 0, -10, 0)).Should().ContainSingle().Which.Str.Should().Be("negative");
    }

    [Fact]
    public void Query_EnvelopeFarBeyondTheData_OnlyVisitsOccupiedCells()
    {
        // A 1 m grid over ±1000 km would be 4·10¹² cells if walked blindly.
        var index = Build(1, (5, 5, "a"), (7, 9, "b"));

        index.Query(new Envelope(-1e6, 1e6, -1e6, 1e6)).Should().HaveCount(2);
    }

    [Fact]
    public void Query_NoAddresses_FindsNothing() =>
        new PreloadedAddresses.Builder().Build(expectedCount: 0).Query(new Envelope(0, 10, 0, 10)).Should().BeEmpty();

    [Fact]
    public void Query_ReturnsEveryFieldAsAdded()
    {
        var builder = new PreloadedAddresses.Builder();
        builder.Add(new AddressFeature(new Point(412.5, 5_300_000.25), "Hauptstraße", "12", "a", "79098", "Herdern", "Freiburg im Breisgau"));
        builder.Add(1000, 1000, null, null, null, null, null, null);
        var index = builder.Build(expectedCount: 2);

        var address = index.Query(new Envelope(0, 500, 5_000_000, 6_000_000)).Should().ContainSingle().Subject;
        address.Should().BeEquivalentTo(new
        {
            Str = "Hauptstraße", Hnr = "12", HnrZus = "a", Plz = "79098", Ort = "Herdern", Gemeinde = "Freiburg im Breisgau",
        });
        address.Location.X.Should().Be(412.5);
        address.Location.Y.Should().Be(5_300_000.25);

        index.Query(new Envelope(999, 1001, 999, 1001)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Str = (string?)null, Hnr = (string?)null, Ort = (string?)null });
    }

    [Fact]
    public void Builder_StoresEachDistinctValueOnce()
    {
        // Millions of addresses share a few thousand street names: equal names built from
        // separate strings must come back as one instance, not one copy per address.
        var builder = new PreloadedAddresses.Builder();
        builder.Add(1, 1, new string("Lindenweg".AsSpan()), "1", null, null, "Ort", "Gemeinde");
        builder.Add(2, 2, new string("Lindenweg".AsSpan()), "2", null, null, "Ort", "Gemeinde");
        var index = builder.Build(expectedCount: 2);

        var streets = index.Query(new Envelope(0, 10, 0, 10)).Select(a => a.Str).ToList();

        streets.Should().HaveCount(2);
        ReferenceEquals(streets[0], streets[1]).Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_IsNeverFull_AndCarriesTheExpectedCount()
    {
        var index = Build(10, (5, 5, "a"));

        var tile = await index.GetAsync(new Tile(0, 0, 10, 10), TestContext.Current.CancellationToken);

        tile.IsFull.Should().BeFalse();
        tile.Features.Should().ContainSingle();
        index.ExpectedCount.Should().Be(1);
    }
}
