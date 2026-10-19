using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.Roads;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Tests;

public class RoadMeshLayoutTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RoundedHighwayTangencyDoesNotMiterBackIntoTheStraightLegNeighbor(int rotation)
    {
        var map = new GameMap(80, 80, new GameContent());
        Vector2 Rotate(Vector2 point)
        {
            for (int turn = 0; turn < rotation; turn++) point = new(80 - point.Y, point.X);
            return point;
        }
        var points = new[] { new Vector2(8, 20), new(20, 20), new(20.5f, 20.02f),
            new(21, 20.08f), new(22, 20.35f) }.Select(Rotate).ToArray();
        var path = new RoadPath(map.Content.Roads.Get(DefaultRoads.KingsRoadName), true, false,
            points.Select(p => (double)p.X * RoadCurves.CellWidth).ToArray(),
            points.Select(p => (double)p.Y * RoadCurves.CellHeight).ToArray());
        var layout = new RoadMeshLayout(map, [path]);
        foreach (int side in new[] { -1, 1 })
        {
            var center = Rotate(new(19.5f, side < 0 ? 18.5f : 21.5f));
            int x = (int)MathF.Floor(center.X), y = (int)MathF.Floor(center.Y);
            Assert.DoesNotContain(layout.Segments, s => RoadMeshLayout.OverlapsCell(s.Road, x, y));
            Assert.False(new RoadGeometry(map, [path]).OverlapsCell(x, y));
        }
        var curvedNeighbor = Rotate(new(20.5f, 21.5f));
        int curvedX = (int)MathF.Floor(curvedNeighbor.X), curvedY = (int)MathF.Floor(curvedNeighbor.Y);
        Assert.Contains(layout.Segments, s => RoadMeshLayout.OverlapsCell(s.Road, curvedX, curvedY));
        Assert.True(new RoadGeometry(map, [path]).OverlapsCell(curvedX, curvedY));
        Assert.All(layout.Segments.Zip(layout.Segments.Skip(1)), pair =>
        {
            Assert.Equal(pair.First.LeftB, pair.Second.LeftA);
            Assert.Equal(pair.First.RightB, pair.Second.RightA);
        });
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 1)]
    public void ShoreRoadDeadEndPaddingStopsBeforeWaterInEveryDirection(int directionX, int directionY)
    {
        var map = new GameMap(24, 24, new GameContent());
        map.SetTerrain(12 + directionX, 12 + directionY, map.Content.Terrains.Get("Water"));
        for (int step = 0; step < 4; step++)
            map.SetRoad(12 - directionX * step, 12 - directionY * step, true);
        var path = Assert.Single(RoadCurves.Extract(map, (_, _) => true));
        var ends = new[]
        {
            new Vector2((float)path.Xs[0] / RoadCurves.CellWidth, (float)path.Ys[0] / RoadCurves.CellHeight),
            new Vector2((float)path.Xs[^1] / RoadCurves.CellWidth, (float)path.Ys[^1] / RoadCurves.CellHeight)
        };
        Assert.Contains(ends, point => Vector2.DistanceSquared(point, new(12.5f, 12.5f)) < 0.000001f);
        var layout = new RoadMeshLayout(map, [path]);
        Assert.All(layout.Segments, segment => Assert.False(segment.Bridge));
        Assert.Equal(4, map.RoadCells.Count);
        Assert.False(map.HasRoad(12 + directionX, 12 + directionY));
    }

    [Fact]
    public void DryRoadDeadEndsOnNarrowBanksDoNotBecomeHalfBuiltBridges()
    {
        var map = new GameMap(24, 24, new GameContent());
        for (int row = 0; row < map.Height; row++)
            for (int column = 8; column < map.Width; column++)
                map.SetTerrain(column, row, map.Content.Terrains.Get("Water"));
        map.SetTerrain(8, 12, map.Content.Terrains.Get("Meadow"));
        var path = new RoadPath(map.Content.Roads.Default, true, false,
            [3.5 * RoadCurves.CellWidth, 8.5 * RoadCurves.CellWidth],
            [12.5 * RoadCurves.CellHeight, 12.5 * RoadCurves.CellHeight]);
        Assert.True(TerrainSprite.WaterSurface(map, 8.5, 12.5) >= ShorelineLayout.WaterThreshold);
        var layout = new RoadMeshLayout(map, [path]);
        Assert.All(layout.Segments, segment => Assert.False(segment.Bridge));
        Assert.All(layout.Segments, segment =>
        {
            Assert.Equal(0, segment.HeightA);
            Assert.Equal(0, segment.HeightB);
        });
    }

    private static RoadPath Path(GameMap map, bool closed, params Vector2[] points) =>
        new(map.Content.Roads.Default, true, closed, points.Select(p => (double)p.X * 12).ToArray(),
            points.Select(p => (double)p.Y * 22).ToArray());

    [Fact]
    public void RoadAndShoulderEdgesAreOffsetsOfTheSameCenterline()
    {
        var map = TestCity.Flat().Map;
        var layout = new RoadMeshLayout(map, [Path(map, false, new(5, 5), new(15, 5))]);
        Assert.NotEmpty(layout.Segments);
        foreach (var segment in layout.Segments)
        {
            Assert.All(segment.Road, p => Assert.Equal(RoadMeshLayout.HalfWidth, Math.Abs(p.Y - 5), 5));
            Assert.All(segment.Corridor, p => Assert.Equal(RoadMeshLayout.HalfWidth + RoadMeshLayout.ShoulderWidth,
                Math.Abs(p.Y - 5), 5));
        }
        Assert.NotNull(layout.RoadAt(new(10, 5.2f)));
        Assert.Null(layout.RoadAt(new(10, 5.55f)));
        Assert.True(layout.InCorridor(new(10, 5.55f)));
        Assert.False(layout.InCorridor(new(10, 5.65f)));
    }

    [Theory]
    [InlineData(DefaultRoads.TrackName, 1f)]
    [InlineData(DefaultRoads.CobbledName, 1f)]
    [InlineData(DefaultRoads.KingsRoadName, 2f)]
    public void RoadWidthsPickingAndShouldersUseWorldCellsAtEveryAngle(string name, float width)
    {
        var map = TestCity.Flat().Map;
        var type = map.Content.Roads.Get(name);
        Assert.Equal(width, type.WidthCells);
        foreach (var direction in new[] { Vector2.UnitX, Vector2.UnitY, Vector2.Normalize(new Vector2(3, 4)) })
        {
            var center = new Vector2(10, 10);
            var normal = new Vector2(-direction.Y, direction.X);
            var path = new RoadPath(type, true, false,
                [(center.X - direction.X * 5) * 12, (center.X + direction.X * 5) * 12],
                [(center.Y - direction.Y * 5) * 22, (center.Y + direction.Y * 5) * 22]);
            var layout = new RoadMeshLayout(map, [path]);
            Assert.All(layout.Segments, segment =>
            {
                Assert.All(segment.Road, p => Assert.Equal(width / 2, Math.Abs(Vector2.Dot(p - center, normal)), 5));
                Assert.All(segment.Corridor, p => Assert.Equal(width / 2 + RoadMeshLayout.ShoulderWidth,
                    Math.Abs(Vector2.Dot(p - center, normal)), 5));
            });
            Assert.NotNull(layout.RoadAt(center + normal * (width / 2 - 0.01f)));
            Assert.Null(layout.RoadAt(center + normal * (width / 2 + 0.01f)));
            Assert.True(layout.InCorridor(center + normal * (width / 2 + 0.1f)));
            Assert.True(layout.TryHeightAt(center + normal * (width / 2 + 0.1f), out _));
        }
    }

    [Fact]
    public void HighwayRightAngleLayoutBecomesABroadContinuousCurve()
    {
        var map = new GameMap(48, 48, new GameContent());
        var highway = map.Content.Roads.Get(DefaultRoads.KingsRoadName);
        for (int x = 3; x <= 24; x++) map.SetRoad(x, 24, highway);
        for (int y = 24; y <= 44; y++) map.SetRoad(24, y, highway);
        var path = Assert.Single(RoadCurves.Extract(map, (_, _) => true));
        var layout = new RoadMeshLayout(map, [path]);
        var corner = new Vector2(24, 25);
        Assert.True(layout.Segments.Min(s => Vector2.Distance(s.A, corner)) > 2.4f,
            "A six-cell highway radius must not collapse into a small rounded right-angle knee.");
        for (int i = 1; i < layout.Segments.Count; i++)
        {
            var previous = layout.Segments[i - 1];
            var current = layout.Segments[i];
            Assert.Equal(previous.B, current.A);
            double angle = Math.Acos(Math.Clamp(Vector2.Dot(Vector2.Normalize(previous.B - previous.A),
                Vector2.Normalize(current.B - current.A)), -1, 1)) * 180 / Math.PI;
            Assert.InRange(angle, 0, 15);
            Assert.NotNull(layout.RoadAt(current.A));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HighwayBedsAndSelectionOccupyExactlyTwoWholeCellBands(bool vertical)
    {
        var map = new GameMap(32, 32, new GameContent());
        var type = map.Content.Roads.Get(DefaultRoads.KingsRoadName);
        for (int i = 4; i <= 26; i++) map.SetRoad(vertical ? 10 : i, vertical ? i : 10, type);
        var paths = RoadCurves.Extract(map, (_, _) => true);
        var layout = new RoadMeshLayout(map, paths);
        var geometry = new RoadGeometry(map, paths);
        var point = vertical ? new Vector2(11.25f, 13.25f) : new Vector2(13.25f, 11.25f);
        var selection = layout.SelectionPolygon(point);
        Assert.Equal(4, selection.Length);
        Assert.All(selection, p => Assert.Equal(MathF.Round(vertical ? p.X : p.Y), vertical ? p.X : p.Y));
        Assert.Equal(10, selection.Min(p => vertical ? p.X : p.Y));
        Assert.Equal(12, selection.Max(p => vertical ? p.X : p.Y));
        Assert.Equal(13, selection.Min(p => vertical ? p.Y : p.X));
        Assert.Equal(14, selection.Max(p => vertical ? p.Y : p.X));
        Assert.False(geometry.OverlapsCell(vertical ? 9 : 13, vertical ? 13 : 9));
        Assert.True(geometry.OverlapsCell(vertical ? 10 : 13, vertical ? 13 : 10));
        Assert.True(geometry.OverlapsCell(vertical ? 11 : 13, vertical ? 13 : 11));
        Assert.False(geometry.OverlapsCell(vertical ? 12 : 13, vertical ? 13 : 12));
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    public void CoarseRoadSelectionContainsBothHalvesOfAnOddAlignedHighway(bool vertical, int stride)
    {
        var map = new GameMap(40, 40, new GameContent());
        var type = map.Content.Roads.Get(DefaultRoads.KingsRoadName);
        for (int i = 4; i <= 32; i++) map.SetRoad(vertical ? 11 : i, vertical ? i : 11, type);
        var layout = new RoadMeshLayout(map, RoadCurves.Extract(map, (_, _) => true));
        var point = vertical ? new Vector2(12.25f, 17.25f) : new Vector2(17.25f, 12.25f);
        var polygon = layout.SelectionPolygon(point, stride);
        Assert.True(polygon.Length >= 4);
        Assert.True(polygon.Min(p => vertical ? p.X : p.Y) <= 11);
        Assert.True(polygon.Max(p => vertical ? p.X : p.Y) >= 13);
        Assert.Equal(stride, polygon.Max(p => vertical ? p.Y : p.X) - polygon.Min(p => vertical ? p.Y : p.X), 4);
    }

    [Fact]
    public void SingleClickedCellRescalesAsOneBlockWhileNormalSelectionStaysOneCell()
    {
        var session = new TermCity.Core.Session.GameSession(TestCity.Flat());
        session.SelectCell(new(13, 11));
        Assert.Equal(1, session.ActiveArea.Area);
        session.SetZoom(-1);
        Assert.Equal(2, session.ActiveArea.Width);
        Assert.Equal(2, session.ActiveArea.Height);
        session.SetZoom(-2);
        Assert.Equal(4, session.ActiveArea.Width);
        Assert.Equal(4, session.ActiveArea.Height);
        session.SetZoom(0);
        Assert.Equal(1, session.ActiveArea.Area);
    }

    [Fact]
    public void OneHighwayAnchorReservesATwoCellPlotNotFourPartialCells()
    {
        var map = new GameMap(32, 32, new GameContent());
        map.SetRoad(10, 10, map.Content.Roads.Get(DefaultRoads.KingsRoadName));
        var paths = RoadCurves.Extract(map, (_, _) => true);
        var layout = new RoadMeshLayout(map, paths);
        var segment = Assert.Single(layout.Segments);
        Assert.Equal(10, segment.Road.Min(p => p.X));
        Assert.Equal(11, segment.Road.Max(p => p.X));
        Assert.Equal(10, segment.Road.Min(p => p.Y));
        Assert.Equal(12, segment.Road.Max(p => p.Y));
        Assert.Equal(new TermCity.Core.Util.Pos(10, 10), Assert.Single(Assert.Single(paths).SourceCells));
    }

    [Fact]
    public void BroadHighwayArcsHaveARealSixCellRadiusAndPickableSelectionEdges()
    {
        var map = new GameMap(64, 64, new GameContent());
        var type = map.Content.Roads.Get(DefaultRoads.KingsRoadName);
        for (int x = 4; x <= 32; x++) map.SetRoad(x, 24, type);
        for (int y = 24; y <= 52; y++) map.SetRoad(32, y, type);
        var path = Assert.Single(RoadCurves.Extract(map, (_, _) => true));
        int measured = 0;
        for (int i = 1; i < path.Count - 1; i++)
        {
            double ax = path.Xs[i - 1] / 12, ay = path.Ys[i - 1] / 22;
            double bx = path.Xs[i] / 12, by = path.Ys[i] / 22;
            double cx = path.Xs[i + 1] / 12, cy = path.Ys[i + 1] / 22;
            double cross = Math.Abs((bx - ax) * (cy - ay) - (by - ay) * (cx - ax));
            if (cross < 1e-8) continue;
            double radius = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)) *
                Math.Sqrt((cx - bx) * (cx - bx) + (cy - by) * (cy - by)) *
                Math.Sqrt((cx - ax) * (cx - ax) + (cy - ay) * (cy - ay)) / (2 * cross);
            Assert.True(radius >= 5.99, $"Highway curvature radius was only {radius:0.000} cells.");
            measured++;
        }
        Assert.True(measured > 10);
        var layout = new RoadMeshLayout(map, [path]);
        var bend = layout.Segments.OrderBy(s => Vector2.DistanceSquared(s.A, new(32, 25))).First();
        var point = (bend.A + bend.B) / 2;
        var selection = layout.SelectionPolygon(point);
        Assert.True(selection.Length >= 4);
        Assert.All(selection, p => Assert.NotNull(layout.RoadAt(p)));
        var guide = new RoadGuide([path]);
        Assert.True(guide.TrySnap(32 * 12, 25 * 22, out double snappedX, out double snappedY));
        Assert.NotNull(layout.RoadAt(new((float)snappedX / 12, (float)snappedY / 22)));
    }

    [Theory]
    [InlineData(DefaultRoads.TrackName)]
    [InlineData(DefaultRoads.KingsRoadName)]
    public void WideMiteredCorridorsRetainGradeSupportAcrossCellBoundaries(string name)
    {
        var map = new GameMap(32, 32, new GameContent());
        for (int y = 4; y < 16; y++)
            for (int x = 7; x < 20; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Hill"));
        var path = new RoadPath(map.Content.Roads.Get(name), true, false,
            [5.5 * 12, 8.5 * 12, 8.75 * 12], [12.5 * 22, 5.5 * 22, 12.5 * 22]);
        var layout = new RoadMeshLayout(map, [path]);
        foreach (var segment in layout.Segments)
            foreach (var point in segment.Corridor)
            {
                Assert.True(layout.TryHeightAt(point, out float height), $"No grade at corridor vertex {point}.");
                Assert.True(float.IsFinite(height));
                Assert.True(float.IsFinite(layout.SupportedShoulderHeight(point, TerrainSprite.HillBaseHeight)));
            }
    }

    [Fact]
    public void HighwayCorridorsAndGradesRemainQueryableAtTheExactMapBoundary()
    {
            var map = new GameMap(32, 32, new GameContent());
            var type = map.Content.Roads.Get(DefaultRoads.KingsRoadName);
            var path = new RoadPath(type, true, false, [10.5 * 12, 10.5 * 12], [25 * 22.0, 32 * 22.0]);
            var layout = new RoadMeshLayout(map, [path]);
            Assert.True(layout.InGroundCorridor(new(10.5f, 32)));
            Assert.True(layout.InCorridor(new(11.6f, 32)));
            Assert.True(layout.TryHeightAt(new(11.6f, 32), out _));
            Assert.True(float.IsFinite(layout.SupportedShoulderHeight(new(11.6f, 32), 0)));
    }

    [Fact]
    public void HighwayGradeSupportUsesItsFullReachRegardlessOfCellBuckets()
    {
            var map = new GameMap(32, 32, new GameContent());
            var type = map.Content.Roads.Get(DefaultRoads.KingsRoadName);
            var path = new RoadPath(type, true, false, [10.5 * 12, 10.5 * 12], [3 * 22.0, 32 * 22.0]);
            var layout = new RoadMeshLayout(map, [path]);
            foreach (float x in new[] { 11.99999f, 12, 12.00001f, 12.2f })
            {
                Assert.True(layout.TryHeightAt(new(x, 10), out float height));
                Assert.Equal(0, height);
                Assert.True(layout.TryHeightAt(new(x, 32), out _));
            }
            Assert.False(layout.TryHeightAt(new(12.75f, 10), out _));
    }

    [Fact]
    public void BentRoadEdgesMeetExactlyWithoutShoulderGaps()
    {
        var map = TestCity.Flat().Map;
        var layout = new RoadMeshLayout(map, [Path(map, false, new(5, 5), new(10, 5), new(10, 12))]);
        for (int i = 1; i < layout.Segments.Count; i++)
        {
            var previous = layout.Segments[i - 1]; var current = layout.Segments[i];
            Assert.Equal(previous.B, current.A);
            foreach (var point in previous.Road.Where(p => current.Road.Contains(p)))
                Assert.True(layout.InCorridor(point));
        }
        Assert.NotNull(layout.RoadAt(new(10, 5)));
    }

    [Fact]
    public void GradeQueriesAreContinuousAcrossCellAndChunkBoundaries()
    {
        var map = new GameMap(64, 48, new GameContent());
        for (int y = 7; y < 23; y++)
            for (int x = 8; x < 28; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Hill"));
        var layout = new RoadMeshLayout(map, [Path(map, false, new(4, 14.5f), new(34, 14.5f))]);
        Assert.True(layout.HeightAt(new(16, 14.5f)) > layout.HeightAt(new(4, 14.5f)));
        for (int x = 5; x < 33; x++)
            Assert.InRange(Math.Abs(layout.HeightAt(new(x - 0.00001f, 14.5f)) -
                layout.HeightAt(new(x + 0.00001f, 14.5f))), 0, 0.0001f);
    }

    [Fact]
    public void RoadSettlesOntoTheSummitWithoutASparseSamplingTail()
    {
        var map = new GameMap(80, 64, new GameContent());
        for (int y = 8; y < 56; y++)
            for (int x = 8; x < 72; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Hill"));
        var layout = new RoadMeshLayout(map, [Path(map, false, new(3.5f, 32.5f), new(76.5f, 32.5f))]);
        for (float x = 16; x <= 64; x += 0.25f)
        {
            float terrace = TerrainSprite.Elevation(map, (int)x, 32);
            Assert.Equal(terrace, layout.HeightAt(new(x, 32.5f)), 5);
            Assert.Equal(terrace, layout.SupportedShoulderHeight(new(x, 32.92f), terrace), 5);
        }
        Assert.True(layout.Segments.Count < 45, "Level stretches must still coalesce for cached terrain rendering.");
    }

    [Fact]
    public void RoadsAlongUpperCoastalTerracesStayAtTheTerraceHeight()
    {
        var map = new GameMap(32, 32, new GameContent());
        for (int row = 4; row < 28; row++)
            for (int column = 4; column < 16; column++)
                map.SetTerrain(column, row, map.Content.Terrains.Get("Hill"));
        for (int row = 0; row < map.Height; row++)
            for (int column = 16; column < map.Width; column++)
                map.SetTerrain(column, row, map.Content.Terrains.Get("Water"));
        float level = TerrainSprite.Elevation(map, 14, 15);
        Assert.True(level > TerrainSprite.HillBaseHeight);
        var layout = new RoadMeshLayout(map, [Path(map, false, new(14.99f, 10.5f), new(14.99f, 20.5f))]);
        Assert.NotEmpty(layout.Segments);
        foreach (var segment in layout.Segments)
        {
            Assert.False(segment.Bridge);
            Assert.Equal(level, segment.HeightA, 5);
            Assert.Equal(level, segment.HeightB, 5);
        }
    }

    [Theory]
    [InlineData(0.03f)]
    [InlineData(-0.03f)]
    [InlineData(0.1f)]
    [InlineData(-0.1f)]
    public void ShallowRoadsideCutsMeetTheTerraceInsteadOfLeavingThinWalls(float terrace)
    {
        var map = TestCity.Flat().Map;
        var layout = new RoadMeshLayout(map, [Path(map, false, new(5, 5), new(15, 5))]);
        foreach (int side in new[] { -1, 1 })
        {
            Assert.Equal(0, layout.SupportedShoulderHeight(new(10, 5 + side * RoadMeshLayout.HalfWidth), terrace), 5);
            Assert.Equal(terrace / 2,
                layout.SupportedShoulderHeight(new(10, 5 + side * (RoadMeshLayout.HalfWidth + RoadMeshLayout.ShoulderWidth / 2)), terrace), 5);
            Assert.Equal(terrace,
                layout.SupportedShoulderHeight(new(10, 5 + side * (RoadMeshLayout.HalfWidth + RoadMeshLayout.ShoulderWidth)), terrace), 5);
        }
    }

    [Fact]
    public void SteepCuttingsKeepSolidEarthFacesRatherThanSmallShoulderWedges()
    {
        var map = TestCity.Flat().Map;
        var layout = new RoadMeshLayout(map, [Path(map, false, new(5, 5), new(15, 5))]);
        foreach (int side in new[] { -1, 1 })
            Assert.Equal(0, layout.SupportedShoulderHeight(
                new(10, 5 + side * (RoadMeshLayout.HalfWidth + RoadMeshLayout.ShoulderWidth)),
                TerrainSprite.HillStepHeight), 5);
    }

    [Fact]
    public void JunctionsResolveRoadTypeWithoutConflictingHeightSources()
    {
        var map = TestCity.Flat().Map;
        var minor = Path(map, false, new(5, 10), new(15, 10));
        var major = new RoadPath(map.Content.Roads.OrderByDescending(r => r.Rank).First(), true, false,
            [10 * 12.0, 10 * 12.0], [5 * 22.0, 15 * 22.0]);
        var layout = new RoadMeshLayout(map, [minor, major]);
        Assert.Same(major.Type, layout.RoadAt(new(10, 10))!.Type);
        Assert.Equal(RoadMeshLayout.SurfaceLift, layout.HeightAt(new(10, 10)), 5);
        Assert.True(layout.InRoadInterior(new(10, 10)));
    }

    [Fact]
    public void ClosedRoadsAndSingleCellsHaveValidCompleteRibbons()
    {
        var map = TestCity.Flat().Map;
        var loop = new RoadMeshLayout(map, [Path(map, true, new(5, 5), new(10, 5), new(10, 10), new(5, 10))]);
        Assert.Equal(loop.Segments[0].A, loop.Segments[^1].B);
        Assert.Equal(loop.Segments[0].HeightA, loop.Segments[^1].HeightB);
        Assert.NotNull(loop.RoadAt(new(5, 7)));
        var isolated = new RoadMeshLayout(map, [Path(map, false, new Vector2(20.5f, 10.5f))]);
        Assert.NotNull(isolated.RoadAt(new(20.5f, 10.5f)));
        Assert.True(isolated.InCorridor(new(20.5f, 10.8f)));
    }

    [Fact]
    public void WaterCrossingsRemainRaisedAndDoNotChangeBridgeGameplay()
    {
        var map = TestCity.Flat().Map;
        for (int y = 0; y < map.Height; y++)
            for (int x = 12; x < 18; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        for (int x = 8; x < 22; x++) map.SetRoad(x, 10, true);
        var layout = new RoadMeshLayout(map, [Path(map, false, new(8.5f, 10.5f), new(21.5f, 10.5f))]);
        Assert.True(layout.HeightAt(new(15.5f, 10.5f)) > 0.05f);
        Assert.True(map.HasRoad(15, 10));
        Assert.False(map.TerrainAt(15, 10).Buildable);
        Assert.NotNull(layout.RoadAt(new(15.5f, 10.5f)));
        Assert.True(layout.RoadAt(new(15.5f, 10.5f))!.Bridge);
    }

    [Fact]
    public void PlayerRoadStrokeAcrossWaterBuildsAndRendersTheEntireBridge()
    {
        var game = TestCity.Flat();
        var map = game.Map;
        for (int y = 2; y < map.Height - 2; y++)
            for (int x = 12; x < 24; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        game.Touch();
        var stroke = Enumerable.Range(8, 20).Select(x => new TermCity.Core.Util.Pos(x, 10)).ToArray();
        var quote = game.QuoteRoad(stroke);
        Assert.Equal(stroke.Length, quote.Cells);
        var result = game.BuildRoad(stroke);
        Assert.True(result.Success, result.Message);
        var layout = new RoadMeshLayout(map, game.Network.DrawnPaths);
        foreach (var p in stroke)
        {
            Assert.True(map.HasRoad(p.X, p.Y));
            var segment = layout.RoadAt(new(p.X + 0.5f, p.Y + 0.5f));
            Assert.NotNull(segment);
            if (!map.TerrainAt(p.X, p.Y).Buildable)
            {
                Assert.True(segment.Bridge);
                Assert.True(layout.HeightAt(new(p.X + 0.5f, p.Y + 0.5f)) > 0.17f);
            }
        }
        var restored = TermCity.Core.Persistence.SaveGameStore.Deserialize(
            TermCity.Core.Persistence.SaveGameStore.Serialize(game));
        foreach (var p in stroke) Assert.True(restored.Map.HasRoad(p.X, p.Y));
    }

    [Theory]
    [InlineData(8, 10, 28, 26)]
    [InlineData(28, 26, 8, 10)]
    [InlineData(8, 26, 28, 10)]
    [InlineData(28, 10, 8, 26)]
    [InlineData(8, 10, 28, 30)]
    public void AngledPlayerCrossingsRemainOneConnectedRoadWithBothBridgeApproaches(
        int ax, int ay, int bx, int by)
    {
        var game = TestCity.Flat();
        var map = game.Map;
        foreach (int cell in map.RoadCells.ToArray()) map.SetRoad(cell % map.Width, cell / map.Width, false);
        for (int y = 0; y < map.Height; y++)
            for (int x = 14; x <= 21; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        game.Touch();
        var session = new TermCity.Core.Session.GameSession(game);
        session.PlaceCursor(new(ax, ay));
        session.BeginRoadLine();
        session.UpdateDrag(new(bx, by));
        var stroke = TermCity.Core.Util.CellLines.Between(new(ax, ay), new(bx, by));
        Assert.Equal(stroke.Count, session.Preview!.Quote.Cells);
        var result = session.ConfirmPreview();
        Assert.True(result.Success, result.Message);
        foreach (var p in stroke) Assert.True(map.HasRoad(p.X, p.Y), $"Stroke lost {p}.");
        var paths = game.Network.DrawnPaths;
        var layout = new RoadMeshLayout(map, paths);
        Assert.Single(paths);
        Assert.Contains(layout.Segments, s => s.Bridge);
        for (int i = 1; i < layout.Segments.Count; i++)
            Assert.Equal(layout.Segments[i - 1].B, layout.Segments[i].A);
    }

    [Theory]
    [InlineData(8, 10, 28, 26)]
    [InlineData(28, 26, 8, 10)]
    [InlineData(8, 26, 28, 10)]
    public void SelectedRiverCrossingBuildsOneStrokeInsteadOfScatteredRoadsInTheRectangle(
        int ax, int ay, int bx, int by)
    {
        var game = TestCity.Flat();
        var map = game.Map;
        foreach (int cell in map.RoadCells.ToArray()) map.SetRoad(cell % map.Width, cell / map.Width, false);
        for (int y = 0; y < map.Height; y++)
            for (int x = 14; x <= 21; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        game.Touch();
        var session = new TermCity.Core.Session.GameSession(game);
        session.BeginDrag(new(ax, ay));
        session.UpdateDrag(new(bx, by));
        session.EndSelection();
        session.PreviewRoad();
        var stroke = TermCity.Core.Util.CellLines.Between(new(ax, ay), new(bx, by));
        Assert.Equal(stroke.Count, session.Preview!.Quote.Cells);
        Assert.True(session.ConfirmPreview().Success);
        Assert.Equal(stroke.Count, map.RoadCells.Count);
        Assert.Single(game.Network.DrawnPaths);
    }

    [Fact]
    public void ShortDeepValleysBecomeBridgeSpansInsteadOfSteepDips()
    {
        var map = new GameMap(48, 32, new GameContent());
        var hill = map.Content.Terrains.Get("Hill");
        for (int y = 5; y < 25; y++)
            for (int x = 4; x < 35; x++)
                if (x < 17 || x > 20) map.SetTerrain(x, y, hill);
        var layout = new RoadMeshLayout(map, [Path(map, false, new(8.5f, 15.5f), new(31.5f, 15.5f))]);
        Assert.Contains(layout.Segments, segment => segment.Bridge && segment.A.X < 19 && segment.B.X > 17);
        Assert.True(layout.HeightAt(new(19, 15.5f)) > 0.3f);
        Assert.Equal(0, TerrainSprite.Elevation(map, 19, 15));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(80)]
    public void ThinTerraceDetectionIsIndependentOfRoadAngle(float angle)
    {
        var rotation = Matrix3x2.CreateRotation(angle * MathF.PI / 180);
        Vector2[] thin = [new(0, 0), new(2, 0), new(2, 0.1f), new(0, 0.1f)];
        Vector2[] broad = [new(0, 0), new(2, 0), new(2, 0.6f), new(0, 0.6f)];
        Assert.True(RoadMeshLayout.IsThinRemnant(thin.Select(p => Vector2.Transform(p, rotation)).ToArray()));
        Assert.False(RoadMeshLayout.IsThinRemnant(broad.Select(p => Vector2.Transform(p, rotation)).ToArray()));
    }

    [Theory]
    [InlineData(DefaultRoads.TrackName, 0.12f)]
    [InlineData(DefaultRoads.CobbledName, 0.075f)]
    [InlineData(DefaultRoads.KingsRoadName, 0.1f)]
    public void CurvedHillRoadCurbsShareOuterAndInnerMiterEndpoints(string name, float width)
    {
        var map = new GameMap(32, 32, new GameContent());
        for (int y = 8; y < 24; y++)
            for (int x = 14; x < 28; x++)
                map.SetTerrain(x, y, map.Content.Terrains.Get("Hill"));
        var path = new RoadPath(map.Content.Roads.Get(name), true, false,
            [6.5 * 12, 12.5 * 12, 18.5 * 12, 24.5 * 12],
            [12.5 * 22, 12.5 * 22, 14.5 * 22, 18.5 * 22]);
        var layout = new RoadMeshLayout(map, [path]);
        Assert.Contains(layout.Segments, segment => segment.HeightA != segment.HeightB);
        foreach (bool left in new[] { true, false })
            for (int i = 1; i < layout.Segments.Count; i++)
            {
                var before = layout.BorderBand(layout.Segments[i - 1], left, width);
                var after = layout.BorderBand(layout.Segments[i], left, width);
                Assert.Equal(before[1], after[0]);
                Assert.Equal(before[2], after[3]);
                Assert.Equal(layout.HeightAt(before[2]), layout.HeightAt(after[3]));
                Assert.True(layout.InCorridor(after[3]));
                Assert.NotNull(layout.RoadAt(after[3]));
            }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CellClippedCurbsInterpolateTheSharedMiterInsteadOfOffsettingEachFragment(bool left)
    {
        var map = TestCity.Flat().Map;
        var layout = new RoadMeshLayout(map,
            [Path(map, false, new(5, 5), new(10, 5), new(15, 8))]);
        var segment = layout.Segments.First(s =>
            Math.Abs((s.LeftB - s.B).X - (s.LeftA - s.A).X) > 0.01f);
        var source = layout.BorderBand(segment, left, 0.1f);
        var a = Vector2.Lerp(source[0], source[1], 0.2f);
        var b = Vector2.Lerp(source[0], source[1], 0.8f);
        Assert.True(layout.TryBorderBand(a, b, segment.Type, 0.1f, out var clipped));
        Assert.True(Vector2.Distance(clipped[2], Vector2.Lerp(source[3], source[2], 0.8f)) < 0.00001f);
        Assert.True(Vector2.Distance(clipped[3], Vector2.Lerp(source[3], source[2], 0.2f)) < 0.00001f);
        Assert.True(layout.TryBorderBand(b, a, segment.Type, 0.1f, out var reversed));
        Assert.Equal(clipped[3], reversed[2]);
        Assert.Equal(clipped[2], reversed[3]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(80)]
    public void UnsupportedAcuteTerraceTipsAreTrimmedToSupportedWidthWithoutRemovingTheBroadRemainder(float angle)
    {
        var rotation = Matrix3x2.CreateRotation(angle * MathF.PI / 180);
        Vector2 Rotate(Vector2 point) => Vector2.Transform(point, rotation);
        Vector2[] terrace = [Rotate(Vector2.Zero), Rotate(new(1, 0)), Rotate(new(1, 0.3f))];
        Assert.False(RoadMeshLayout.IsThinRemnant(terrace));
        var tip = RoadMeshLayout.UnsupportedTerraceTip(terrace[1], terrace[0], terrace[2]);
        Assert.Equal(3, tip.Length);
        Assert.True(Vector2.Distance(tip[2], terrace[0]) > 0.8f);
        Assert.Equal(RoadMeshLayout.MinimumTerraceWidth, Vector2.Distance(tip[1], tip[2]), 5);
        Assert.True(RoadMeshLayout.Contains(tip, Rotate(new(0.5f, 0.1f))));
        Assert.False(RoadMeshLayout.Contains(tip, Rotate(new(0.95f, 0.27f))));
        Assert.All(tip, point => Assert.True(RoadMeshLayout.Contains(terrace, point)));
    }

    [Fact]
    public void BroadAndRightAngledTerraceCornersAreNotAcuteUnsupportedTips()
    {
        Assert.Empty(RoadMeshLayout.UnsupportedTerraceTip(new(1, 0), Vector2.Zero, new(0, 1)));
        Assert.Empty(RoadMeshLayout.UnsupportedTerraceTip(new(1, 0), Vector2.Zero, new(-1, 0.3f)));
        Assert.Empty(RoadMeshLayout.UnsupportedTerraceTip(new(0.1f, 0), Vector2.Zero, new(1, 0.3f)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(80)]
    public void TerraceJoinsReplaceSharpTipsWithABoundedSmoothCurve(float angle)
    {
        var rotation = Matrix3x2.CreateRotation(angle * MathF.PI / 180);
        var before = Vector2.Transform(new(1, 0), rotation);
        var after = Vector2.Transform(new(0, 1), rotation);
        var clearance = RoadMeshLayout.RoundedTerraceCorner(before, Vector2.Zero, after);
        Assert.Equal(Vector2.Zero, clearance[0]);
        Assert.Equal(before * 0.35f, clearance[1]);
        Assert.Equal(after * 0.35f, clearance[^1]);
        for (int i = 2; i < clearance.Length - 1; i++)
        {
            var incoming = Vector2.Normalize(clearance[i] - clearance[i - 1]);
            var outgoing = Vector2.Normalize(clearance[i + 1] - clearance[i]);
            Assert.True(Vector2.Dot(incoming, outgoing) > MathF.Cos(15 * MathF.PI / 180),
                "Rounded cut joins must not introduce another sharp corner.");
            Assert.InRange(clearance[i].Length(), 0.12f, 0.35f);
        }
    }

    [Fact]
    public void GeometryIsDerivedWithoutChangingSavedGameplayLayers()
    {
        var game = CityGame.New(new GameConfig { Seed = 42 });
        string before = TermCity.Core.Persistence.SaveGameStore.Serialize(game);
        var layout = new RoadMeshLayout(game.Map, game.Network.DrawnPaths);
        Assert.NotEmpty(layout.Segments);
        foreach (var segment in layout.Segments)
            Assert.True(float.IsFinite(layout.HeightAt((segment.A + segment.B) / 2)));
        Assert.Equal(before, TermCity.Core.Persistence.SaveGameStore.Serialize(game));
    }
}
