using System.Numerics;
using TermCity.Core.World;
using TermCity.Core.Rendering;

namespace TermCity.Tests;

public class WallNetworkTests
{
    private static WallNetworkSpan[] Loop(int firstId, params Vector2[] points) => points.Select((p, i) =>
        new WallNetworkSpan(firstId + i, p, points[(i + 1) % points.Length])).ToArray();

    [Fact]
    public void DiagonalAndConcaveFacesAreNotTheirBoundingBoxes()
    {
        var diamond = new WallNetwork(Loop(1, new(5, 0), new(10, 5), new(5, 10), new(0, 5)), new(5, 5));
        Assert.True(diamond.Contains(new(5, 5)));
        Assert.False(diamond.Contains(new(1, 1)));
        Assert.False(diamond.Contains(new(5, 0)));
        var concave = new WallNetwork(Loop(1, new(0, 0), new(10, 0), new(10, 4), new(4, 4),
            new(4, 10), new(0, 10)), new(2, 2));
        Assert.True(concave.Contains(new(2, 8)));
        Assert.False(concave.Contains(new(8, 8)));
    }

    [Fact]
    public void DisconnectedLoopNeedsARealWallConnection()
    {
        var main = Loop(1, new(0, 0), new(10, 0), new(10, 10), new(0, 10));
        var outpost = Loop(10, new(20, 0), new(30, 0), new(30, 10), new(20, 10));
        var disconnected = new WallNetwork(main.Concat(outpost), new(5, 5), main.Select(s => s.Id).ToHashSet());
        Assert.False(disconnected.Contains(new(25, 5)));
        Assert.True(disconnected.ContainsAny(new(25, 5)));
        var connected = new WallNetwork(main.Concat(outpost).Append(new(30, new(10, 5), new(20, 5))), new(5, 5));
        Assert.True(connected.Contains(new(5, 5)));
        Assert.True(connected.Contains(new(25, 5)));
        Assert.False(connected.Contains(new(15, 5)));
    }

    [Fact]
    public void CrossingsAndTJoinsSplitFacesWithoutCreatingDuplicateEdges()
    {
        var box = Loop(1, new(0, 0), new(10, 0), new(10, 10), new(0, 10));
        var network = new WallNetwork(box.Concat([
            new WallNetworkSpan(5, new(0, 5), new(10, 5)),
            new WallNetworkSpan(6, new(5, 0), new(5, 10)),
            new WallNetworkSpan(7, new(2, 0), new(8, 0)),
        ]), new(2, 2));
        Assert.Equal(4, network.Regions.Count);
        Assert.All(new[] { new Vector2(2, 2), new(8, 2), new(2, 8), new(8, 8) }, p => Assert.True(network.Contains(p)));
    }

    [Fact]
    public void NearlyTouchingWallsDoNotCloseAndBreachesOpenTheFace()
    {
        var box = Loop(1, new(0, 0), new(10, 0), new(10, 10), new(0, 10));
        box[3] = box[3] with { B = new(0, .002f) };
        Assert.Empty(new WallNetwork(box, new(5, 5)).Regions);
        box[3] = box[3] with { B = new(0, 0), Intact = false };
        Assert.Empty(new WallNetwork(box, new(5, 5)).Regions);
    }

    [Fact]
    public void GatesCloseTheStructureButPermitWalkingAcrossIt()
    {
        var box = Loop(1, new(0, 0), new(10, 0), new(10, 10), new(0, 10));
        box[0] = box[0] with { IsGate = true };
        var network = new WallNetwork(box, new(5, 5));
        Assert.True(network.Contains(new(5, 5)));
        Assert.False(network.SegmentBlocked(new(5, -1), new(5, 1)));
        Assert.True(network.SegmentBlocked(new(5, -1), new(5, 1), allowGates: false));
        Assert.True(network.SegmentBlocked(new(9, 5), new(11, 5)));
    }

    [Fact]
    public void NetNewLengthExcludesOverlapButNotCrossings()
    {
        var gaps = WallNetwork.UncoveredIntervals(new(0, 0), new(10, 0), [
            new(1, new(2, 0), new(5, 0)), new(2, new(4, 0), new(8, 0)),
            new(3, new(9, -1), new(9, 1)),
        ]);
        Assert.Equal(2, gaps.Count);
        Assert.Equal((0f, .2f), gaps[0]);
        Assert.Equal((.8f, 1f), gaps[1]);
    }

    [Fact]
    public void InvalidCoordinatesAndDuplicateIdsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new WallNetwork([new(1, Vector2.Zero, Vector2.Zero)], Vector2.Zero));
        Assert.Throws<ArgumentException>(() => new WallNetwork([new(1, Vector2.Zero, Vector2.One),
            new(1, Vector2.One, new(2, 2))], Vector2.Zero));
        Assert.Throws<ArgumentException>(() => new WallNetwork([new(1, Vector2.Zero, new(float.NaN, 1))], Vector2.Zero));
    }

    [Fact]
    public void CellLengthWallSectionsKeepAllSourceIdsButOnlyNecessaryPolygonCorners()
    {
        var corners = new[] { new Vector2(0, 0), new(10, 0), new(10, 10), new(0, 10) };
        var spans = corners.SelectMany((a, side) => Enumerable.Range(0, 10).Select(i =>
            new WallNetworkSpan(side * 10 + i + 1, Vector2.Lerp(a, corners[(side + 1) % 4], i / 10f),
                Vector2.Lerp(a, corners[(side + 1) % 4], (i + 1) / 10f)))).ToArray();
        var network = new WallNetwork(spans, new(5, 5));
        var region = Assert.Single(network.Regions);
        Assert.Equal(4, region.Polygon.Count);
        Assert.Equal(40, region.SpanIds.Count);
        for (int y = -1; y <= 10; y++)
            for (int x = -1; x <= 10; x++)
                Assert.Equal(x >= 0 && x < 10 && y >= 0 && y < 10,
                    region.Contains(new(x + .5f, y + .5f)));
    }

    [Fact]
    public void CoplanarJoinClippingProducesDisjointConvexTopsWithoutFilledHoles()
    {
        Vector2[] source = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];
        Vector2[] cutter = [new(4, 4), new(6, 4), new(6, 6), new(4, 6)];
        var pieces = WallDamageLayout.ExposedTop(source, [cutter]);
        double Area(Vector2[] p) => Math.Abs(Enumerable.Range(0, p.Length).Sum(i =>
            (double)p[i].X * p[(i + 1) % p.Length].Y - (double)p[i].Y * p[(i + 1) % p.Length].X)) / 2;
        Assert.Equal(96, pieces.Sum(Area), 5);
        Assert.DoesNotContain(pieces, p => WallNetwork.PointInside(p, new(5, 5)));
    }
}
