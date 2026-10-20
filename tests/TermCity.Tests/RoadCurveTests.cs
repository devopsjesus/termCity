using TermCity.Core.Rendering;
using TermCity.Core.Roads;
using TermCity.Core.Simulation;
using TermCity.Core.World;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class RoadCurveTests
{
    private const string LegacyPaintedRoad = "Legacy painted-road rendering is superseded by dedicated centerline meshes.";
    private const int W = RoadCurves.CellWidth, H = RoadCurves.CellHeight;

    private static (CityGame Game, RoadType King) Blank()
    {
        var game = TestCity.Flat();
        for (int x = 0; x < game.Map.Width; x++)
        {
            game.Map.SetRoad(x, 20, false);
        }

        game.Touch();
        return (game, game.Map.Content.Roads.Get(DefaultRoads.KingsRoadName));
    }

    private static IReadOnlyList<RoadPath> Paths(CityGame game) =>
        RoadCurves.Extract(game.Map, (x, y) => game.Network.IsConnected(game.Map, x, y));

    private static double CentreX(int x) => x * W + W / 2.0;

    private static double CentreY(int y) => y * H + H / 2.0;

    [Fact(Skip = LegacyPaintedRoad)]
    public void GroundRoadClearanceMatchesRasterizedCurvesAcrossChunkBoundaries()
    {
        var (game, king) = Blank();
        for (int x = 4; x < 30; x++) game.Map.SetRoad(x, 10, king);
        game.Touch();
        var roads = new RoadVectorLayer();
        roads.Sync(game);
        for (int cx = 0; cx < 2; cx++)
        {
            var chunk = roads.RenderGround(cx, 0)!;
            Assert.Same(chunk, roads.RenderGround(cx, 0));
            Assert.Contains(chunk.Rgba.Where((_, i) => i % 4 == 3), alpha => alpha is > 0 and < 255);
            for (int y = 8 * H; y < 13 * H; y += 3)
                for (int x = 0; x < chunk.Width; x += 3)
                    Assert.Equal(chunk.Rgba[(y * chunk.Width + x) * 4 + 3] > 0,
                        roads.GroundRoadAt(cx * 16 + (x + 0.5) / W, (y + 0.5) / H));
        }
        Assert.False(roads.GroundRoadAt(-1, -1));
        Assert.True(roads.GroundRoadAt(16, 10.5, 3));
    }

    [Fact(Skip = LegacyPaintedRoad)]
    public void RoadSurfacesHaveDeterministicRestrainedPixelTextureWithoutChangingCoverage()
    {
        foreach (var type in new GameContent().Roads)
        {
            var colors = new HashSet<Rgb>();
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    var color = RoadVectorLayer.GroundSurfaceColor(type, x, y);
                    Assert.Equal(color, RoadVectorLayer.GroundSurfaceColor(type, x, y));
                    Assert.InRange(color.R, (byte)(type.SurfaceColor.R * 0.94), (byte)(type.SurfaceColor.R * 1.06));
                    colors.Add(color);
                }
            Assert.True(colors.Count >= 4, $"{type.Name} must have multiple coarse road-surface shades.");
        }
    }

    [Fact(Skip = LegacyPaintedRoad)]
    public void PavedRoadsDoNotUseDarkPeriodicMortarOutlines()
    {
        var type = new GameContent().Roads.Get(DefaultRoads.KingsRoadName);
        for (int y = 0; y < 36; y++)
            for (int x = 0; x < 36; x++)
            {
                var color = RoadVectorLayer.GroundSurfaceColor(type, x, y);
                Assert.True(color.R >= type.SurfaceColor.R * 0.94,
                    "Paved texture must not contain high-contrast dark mortar lines.");
                if (y % 4 != 0)
                    Assert.Equal(color, RoadVectorLayer.GroundSurfaceColor(type, x, y - 1));
            }
    }

    [Fact]
    public void SubpixelRoadSimplificationPreservesEndpointsAndCurvedShape()
    {
        var points = Enumerable.Range(0, 101).Select(i => (X: i * 0.5, Y: Math.Sin(i * 0.025) * 10)).ToList();
        var simplified = RoadCurves.Straighten(points, new bool[points.Count], 0.2);
        Assert.Equal(points[0], simplified[0]);
        Assert.Equal(points[^1], simplified[^1]);
        Assert.True(simplified.Count < points.Count / 2);
        foreach (var point in points)
        {
            double nearest = double.PositiveInfinity;
            for (int i = 1; i < simplified.Count; i++)
            {
                var a = simplified[i - 1]; var b = simplified[i];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double t = Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
                nearest = Math.Min(nearest, Math.Sqrt(Math.Pow(point.X - a.X - t * dx, 2) +
                    Math.Pow(point.Y - a.Y - t * dy, 2)));
            }
            Assert.InRange(nearest, 0, 0.2);
        }
    }

    [Fact(Skip = LegacyPaintedRoad)]
    public void GroundRoadJunctionUsesOneOpaqueSurfaceWithoutTerminalMarkings()
    {
        var (game, king) = Blank();
        var track = game.Map.Content.Roads.Get(DefaultRoads.TrackName);
        for (int x = 2; x < 30; x++) game.Map.SetRoad(x, 10, king);
        for (int y = 2; y < 15; y++) game.Map.SetRoad(10, y, track);
        game.Touch();
        var roads = new RoadVectorLayer();
        roads.Sync(game);
        var chunk = roads.RenderGround(0, 0)!;
        var terrain = TerrainSprite.Chunk(game.Map, 0, 0, 1, 0);
        var original = terrain.Pixels.ToArray();
        chunk.PaintOver(terrain);
        int center = ((10 * H + H / 2) * chunk.Width + 10 * W + W / 2) * 4;
        Assert.Equal(255, chunk.Rgba[center + 3]);
        Assert.Contains((chunk.Rgba[center], chunk.Rgba[center + 1], chunk.Rgba[center + 2]),
            new[] { RoadVectorLayer.GroundSurfaceColor(king, 10 * W + W / 2, 10 * H + H / 2),
                RoadVectorLayer.GroundSurfaceColor(track, 10 * W + W / 2, 10 * H + H / 2) }
                .Select(color => (color.R, color.G, color.B)));
        for (int offset = 0; offset < chunk.Rgba.Length; offset += 4)
        {
            Assert.Equal(255, terrain.Pixels[offset + 3]);
            if (chunk.Rgba[offset + 3] == 0)
                Assert.Equal(original.AsSpan(offset, 4).ToArray(), terrain.Pixels.AsSpan(offset, 4).ToArray());
            if (chunk.Rgba[offset + 3] == 255)
                Assert.Equal(chunk.Rgba.AsSpan(offset, 3).ToArray(), terrain.Pixels.AsSpan(offset, 3).ToArray());
        }
        Assert.Throws<ArgumentException>(() => chunk.PaintOver(TerrainSprite.Ground(game.Map, 0, 0)));
    }

    [Fact]
    public void EveryRoadTypeIsDrawnAsACurve()
    {
        var game = TestCity.Flat();
        Assert.NotEmpty(Paths(game));
        foreach (var road in game.Map.Content.Roads)
        {
            var (blank, _) = Blank();
            var type = blank.Map.Content.Roads.Get(road.Name);
            for (int x = 5; x < 25; x++) blank.Map.SetRoad(x, 10, type);
            var path = Assert.Single(Paths(blank));
            Assert.Equal(road.Name, path.Type.Name);
            double centre = CentreY(10) + (type.WidthCells % 2 == 0 ? H / 2.0 : 0);
            Assert.All(path.Ys, y => Assert.Equal(centre, y, 6));
        }
    }

    [Fact]
    public void RoadsOfDifferentTypesJoinSmoothlyEachInItsOwnColours()
    {
        var (game, king) = Blank();
        var track = game.Map.Content.Roads.Get(DefaultRoads.TrackName);
        for (int x = 5; x < 15; x++) game.Map.SetRoad(x, 10, track);
        for (int x = 15; x < 25; x++) game.Map.SetRoad(x, 10, king);
        var paths = Paths(game);
        Assert.Equal(2, paths.Count);
        var a = paths.Single(p => p.Type == track);
        var b = paths.Single(p => p.Type == king);
        Assert.True(a.Xs.Max() >= b.Xs.Min(), "the two stretches should overlap, not leave a gap");
        Assert.All(a.Ys, y => Assert.Equal(CentreY(10), y, 6));
        Assert.All(b.Ys, y => Assert.Equal(CentreY(10) + H / 2.0, y, 6));
        var layout = new RoadMeshLayout(game.Map, paths);
        Assert.NotNull(layout.RoadAt(new(15, 10.5f)));
    }

    [Fact]
    public void ARingOfMixedTypesIsCutIntoStretches()
    {
        var (game, king) = Blank();
        var track = game.Map.Content.Roads.Get(DefaultRoads.TrackName);
        for (int x = 5; x <= 15; x++)
        {
            var type = x < 10 ? track : king;
            game.Map.SetRoad(x, 5, type);
            game.Map.SetRoad(x, 15, type);
        }

        for (int y = 6; y < 15; y++)
        {
            game.Map.SetRoad(5, y, track);
            game.Map.SetRoad(15, y, king);
        }

        var paths = Paths(game);
        Assert.All(paths, p => Assert.False(p.Closed));
        Assert.Contains(paths, p => p.Type == track);
        Assert.Contains(paths, p => p.Type == king);
    }

    [Fact]
    public void StraightRoadStaysOnItsRow()
    {
        var (game, king) = Blank();
        for (int x = 5; x < 25; x++) game.Map.SetRoad(x, 10, king);
        var path = Assert.Single(Paths(game));
        Assert.False(path.Closed);
        Assert.All(path.Ys, y => Assert.Equal(CentreY(10) + H / 2.0, y, 6));
        Assert.Equal(5 * W, path.Xs.Min(), 6);
        Assert.Equal(25 * W, path.Xs.Max(), 6);
    }

    [Fact]
    public void UnevenStaircaseIsDrawnAsOneStraightLine()
    {
        var (game, king) = Blank();
        // A shallow diagonal laid as uneven steps (runs of 13, 15 and 14 cells, one row each).
        int x = 5, y = 10;
        foreach (int run in new[] { 13, 15, 14, 15 })
        {
            for (int i = 0; i < run; i++) game.Map.SetRoad(x++, y, king);
            y++;
            x--;
            game.Map.SetRoad(x, y - 1, king);
        }

        var path = Assert.Single(Paths(game));
        double x0 = path.Xs[0], y0 = path.Ys[0], x1 = path.Xs[^1], y1 = path.Ys[^1];
        double length = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        double worst = 0;
        for (int i = 0; i < path.Count; i++)
        {
            worst = Math.Max(worst, Math.Abs((y1 - y0) * (path.Xs[i] - x0) - (x1 - x0) * (path.Ys[i] - y0)) / length);
        }

        Assert.True(worst < 3, $"the diagonal wanders {worst:0.0}px off a straight line");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RecurringShallowStaircaseDoesNotPinAnUnusuallyLongInteriorTread(int rotation)
    {
        var map = new GameMap(128, 128, new GameContent());
        var highway = map.Content.Roads.Get(DefaultRoads.KingsRoadName);
        Pos Rotate(Pos cell)
        {
            for (int turn = 0; turn < rotation; turn++) cell = new(127 - cell.Y, cell.X);
            return cell;
        }
        int x = 5, y = 10;
        foreach (int run in new[] { 13, 30, 12, 14 })
        {
            for (int i = 0; i < run; i++)
            {
                var cell = Rotate(new(x++, y));
                map.SetRoad(cell.X, cell.Y, highway);
            }
            x--;
            y++;
        }
        var path = Assert.Single(RoadCurves.Extract(map, (_, _) => true));
        double dx = path.Xs[^1] - path.Xs[0], dy = path.Ys[^1] - path.Ys[0];
        double length = Math.Sqrt(dx * dx + dy * dy);
        double worst = Enumerable.Range(0, path.Count).Max(i =>
            Math.Abs(dy * (path.Xs[i] - path.Xs[0]) - dx * (path.Ys[i] - path.Ys[0])) / length);
        Assert.True(worst < 3, $"An interior staircase tread created a grid knee {worst:0.0}px off the diagonal.");
    }

    [Fact]
    public void OpposingShortStepsDoNotEraseTheSustainedStraightLegBetweenThem()
    {
        var map = new GameMap(80, 40, new GameContent());
        var highway = map.Content.Roads.Get(DefaultRoads.KingsRoadName);
        Pos[] corners = [new(5, 10), new(17, 10), new(17, 11), new(46, 11), new(46, 10), new(57, 10)];
        foreach (var (a, b) in corners.Zip(corners.Skip(1)))
            foreach (var cell in CellLines.Between(a, b)) map.SetRoad(cell.X, cell.Y, highway);
        var path = Assert.Single(RoadCurves.Extract(map, (_, _) => true));
        var middle = Enumerable.Range(0, path.Count).Where(i => path.Xs[i] >= 25 * W && path.Xs[i] <= 40 * W);
        Assert.NotEmpty(middle);
        Assert.All(middle, i => Assert.Equal(12 * H, path.Ys[i], 6));
    }

    [Fact]
    public void ShallowHighwayBetweenRealBendsDoesNotRipple()
    {
        var game = TestCity.Flat();
        var map = game.Map;
        foreach (int index in map.RoadCells.ToArray())
        {
            var p = map.PosOf(index);
            map.SetRoad(p.X, p.Y, false);
        }
        var highway = map.Content.Roads.Get(TermCity.Core.Roads.DefaultRoads.KingsRoadName);
        Pos[] corners = [new(5, 40), new(5, 25), new(130, 8), new(145, 8)];
        foreach (var (a, b) in corners.Zip(corners.Skip(1)))
            foreach (var p in CellLines.Between(a, b)) map.SetRoad(p.X, p.Y, highway);
        game.Touch();
        var path = Assert.Single(Paths(game));
        var slopes = Enumerable.Range(1, path.Count - 1)
            .Where(i => path.Xs[i] > 25 * W && path.Xs[i] < 110 * W)
            .Select(i => Math.Atan2(path.Ys[i] - path.Ys[i - 1], path.Xs[i] - path.Xs[i - 1]))
            .ToArray();
        Assert.NotEmpty(slopes);
        Assert.True(slopes.Max() - slopes.Min() < 0.03,
            $"The highway's straight run oscillates by {slopes.Max() - slopes.Min():0.000} radians.");
    }

    [Fact]
    public void ClosedHighwayLoopStraightensItsShallowDiagonalRuns()
    {
        var (game, highway) = Blank();
        Pos[] corners = [new(5, 15), new(65, 7), new(65, 25), new(5, 33), new(5, 15)];
        foreach (var (a, b) in corners.Zip(corners.Skip(1)))
            foreach (var p in CellLines.Between(a, b)) game.Map.SetRoad(p.X, p.Y, highway);
        var path = Assert.Single(Paths(game));
        Assert.True(path.Closed);
        var slopes = Enumerable.Range(1, path.Count - 1)
            .Where(i => path.Xs[i] > 20 * W && path.Xs[i] < 50 * W && path.Ys[i] < 17 * H)
            .Select(i => Math.Atan2(path.Ys[i] - path.Ys[i - 1], path.Xs[i] - path.Xs[i - 1]))
            .Select(angle => Math.Atan(Math.Tan(angle))).ToArray();
        Assert.NotEmpty(slopes);
        Assert.True(slopes.Max() - slopes.Min() < 0.03,
            $"A loop's highway run ripples by {slopes.Max() - slopes.Min():0.000} radians.");
    }

    [Fact]
    public void BendIsRoundedAndStaysNearTheCells()
    {
        var (game, king) = Blank();
        for (int x = 5; x <= 20; x++) game.Map.SetRoad(x, 10, king);
        for (int y = 11; y <= 25; y++) game.Map.SetRoad(20, y, king);
        var path = Assert.Single(Paths(game));

        for (int i = 2; i < path.Count; i++)
        {
            double a1 = Math.Atan2(path.Ys[i - 1] - path.Ys[i - 2], path.Xs[i - 1] - path.Xs[i - 2]);
            double a2 = Math.Atan2(path.Ys[i] - path.Ys[i - 1], path.Xs[i] - path.Xs[i - 1]);
            double turn = Math.Abs(Math.Atan2(Math.Sin(a2 - a1), Math.Cos(a2 - a1)));
            Assert.True(turn < 0.45, $"sharp turn of {turn:F2} rad at sample {i}");
        }

        double nearest = Enumerable.Range(0, path.Count).Min(i => Math.Sqrt(
            Math.Pow(path.Xs[i] / W - 20, 2) + Math.Pow(path.Ys[i] / H - 11, 2)));
        Assert.InRange(nearest, 2.4, 2.6);
    }

    [Fact]
    public void DeadEndsRunToTheEdgeOfTheirCell()
    {
        var (game, king) = Blank();
        for (int x = 5; x < 10; x++) game.Map.SetRoad(x, 10, king);
        var path = Assert.Single(Paths(game));
        Assert.Equal(5 * W, path.Xs.Min(), 6);
        Assert.Equal(10 * W, path.Xs.Max(), 6);
    }

    [Fact]
    public void CrossingYieldsTwoStraightPaths()
    {
        var (game, king) = Blank();
        for (int x = 5; x <= 15; x++) game.Map.SetRoad(x, 10, king);
        for (int y = 5; y <= 15; y++) game.Map.SetRoad(10, y, king);
        var paths = Paths(game);
        Assert.Equal(2, paths.Count);
        Assert.Contains(paths, p => p.Ys.All(y => Math.Abs(y - CentreY(10) - H / 2.0) < 1e-6));
        Assert.Contains(paths, p => p.Xs.All(x => Math.Abs(x - CentreX(10) - W / 2.0) < 1e-6));
    }

    [Fact]
    public void BranchReachesTheCentreOfTheRoadItJoins()
    {
        var (game, king) = Blank();
        for (int x = 5; x <= 20; x++) game.Map.SetRoad(x, 10, king);
        for (int y = 11; y <= 18; y++) game.Map.SetRoad(12, y, king);
        var paths = Paths(game);
        Assert.Equal(2, paths.Count);
        var stem = paths.Single(p => p.Xs.All(x => Math.Abs(x - CentreX(12) - W / 2.0) < 1e-6));
        double top = stem.Ys.Min();
        Assert.Equal(CentreY(10) + H / 2.0, top, 6);
    }

    [Theory(Skip = LegacyPaintedRoad)]
    [InlineData(0, 1, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 1)]
    [InlineData(3, 1, 1)]
    [InlineData(0, 2, 1)]
    [InlineData(1, 2, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 2, 1)]
    [InlineData(0, 1, 2)]
    [InlineData(0, 1, 4)]
    public void DirtTrackThreeWayJunctionsMergeWithoutAnEndCap(int rotation, int scale, int stride)
    {
        var (game, _) = Blank();
        var track = game.Map.Content.Roads.Get(DefaultRoads.TrackName);
        (int X, int Y)[] directions = [(0, -1), (1, 0), (0, 1), (-1, 0)];
        game.Map.SetRoad(12, 12, track);
        for (int arm = 0; arm < directions.Length; arm++)
        {
            if (arm == rotation) continue;
            var (dx, dy) = directions[arm];
            for (int step = 1; step <= 10; step++)
                game.Map.SetRoad(12 + dx * step, 12 + dy * step, track);
        }

        var paths = Paths(game);
        Assert.Equal(2, paths.Count);
        Assert.All(paths, path => Assert.Contains(Enumerable.Range(0, path.Count), i =>
            Math.Abs(path.Xs[i] - CentreX(12)) < 1e-6 &&
            Math.Abs(path.Ys[i] - CentreY(12)) < 1e-6));
        AssertDrawnRoadsConnected(game, scale, stride);
    }

    [Fact]
    public void ARoadOfHigherRankRunsWholeThroughAJunctionWithAHumblerOne()
    {
        var (game, king) = Blank();
        var track = game.Map.Content.Roads.Get(DefaultRoads.TrackName);
        for (int x = 5; x <= 20; x++) game.Map.SetRoad(x, 10, king);
        for (int y = 5; y <= 15; y++) if (y != 10) game.Map.SetRoad(12, y, track);
        var paths = Paths(game);
        var kings = paths.Where(p => p.Type == king).ToList();
        var whole = Assert.Single(kings);
        Assert.Equal(5 * W, whole.Xs.Min(), 6);
        Assert.Equal(21 * W, whole.Xs.Max(), 6);
    }

    [Theory(Skip = LegacyPaintedRoad)]
    [InlineData(0, 1, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 1)]
    [InlineData(3, 1, 1)]
    [InlineData(0, 2, 1)]
    [InlineData(0, 1, 2)]
    [InlineData(0, 1, 4)]
    public void OpposingStreetAndHighwayBendsStayVisuallyConnected(int rotation, int scale, int stride)
    {
        var (game, king) = Blank();
        var track = game.Map.Content.Roads.Get(DefaultRoads.TrackName);
        (int X, int Y)[] directions = [(0, -1), (1, 0), (0, 1), (-1, 0)];
        game.Map.SetRoad(12, 12, king);
        for (int arm = 0; arm < directions.Length; arm++)
        {
            var (dx, dy) = directions[arm];
            var type = arm == (rotation + 1) % 4 || arm == (rotation + 2) % 4 ? king : track;
            for (int step = 1; step <= 10; step++)
                game.Map.SetRoad(12 + dx * step, 12 + dy * step, type);
        }

        AssertDrawnRoadsConnected(game, scale, stride);
    }

    [Theory(Skip = LegacyPaintedRoad)]
    [InlineData(1)]
    [InlineData(2)]
    public void ReportedSeedHasNoVisibleGapBetweenStreetsAndHighways(int scale)
    {
        var game = CityGame.New(new GameConfig { Seed = 1981019679, MapWidth = 160, MapHeight = 96 });
        Assert.Equal(game.Map.RoadCells.Count, game.Network.ConnectedRoadCount);
        AssertDrawnRoadsConnected(game, scale);
    }

    private static void AssertDrawnRoadsConnected(CityGame game, int scale = 1, int stride = 1)
    {
        var layer = new RoadVectorLayer();
        layer.Sync(game);
        int chunksX = (game.Map.Width + RoadVectorLayer.ChunkCells * stride - 1) / (RoadVectorLayer.ChunkCells * stride);
        int chunksY = (game.Map.Height + RoadVectorLayer.ChunkCells * stride - 1) / (RoadVectorLayer.ChunkCells * stride);
        int chunkWidth = RoadVectorLayer.ChunkCells * W * scale;
        int chunkHeight = RoadVectorLayer.ChunkCells * H * scale;
        int width = chunksX * chunkWidth, height = chunksY * chunkHeight;
        var painted = new bool[width * height];
        int count = 0, start = -1;
        for (int cy = 0; cy < chunksY; cy++)
        {
            for (int cx = 0; cx < chunksX; cx++)
            {
                if (layer.Render(cx, cy, scale, stride) is not { } chunk) continue;
                for (int y = 0; y < chunk.Height; y++)
                {
                    for (int x = 0; x < chunk.Width; x++)
                    {
                        if (chunk.Rgba[(y * chunk.Width + x) * 4 + 3] <= 32) continue;
                        int index = (cy * chunkHeight + y) * width + cx * chunkWidth + x;
                        painted[index] = true;
                        count++;
                        start = index;
                    }
                }
            }
        }

        Assert.True(start >= 0, "No road pixels were rendered.");
        var queue = new Queue<int>();
        queue.Enqueue(start);
        painted[start] = false;
        int reached = 0;
        (int X, int Y)[] neighbours = [(0, -1), (1, 0), (0, 1), (-1, 0)];
        while (queue.TryDequeue(out int cell))
        {
            reached++;
            int x = cell % width, y = cell / width;
            foreach (var (dx, dy) in neighbours)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                int next = ny * width + nx;
                if (!painted[next]) continue;
                painted[next] = false;
                queue.Enqueue(next);
            }
        }

        Assert.True(reached == count,
            $"Only {reached} of {count} road pixels are joined at scale {scale}, stride {stride}.");
    }

    [Fact(Skip = "Highway-style merge ramps were retired; roads meet at ordinary intersections.")]
    public void ASlantedBranchCurvesInAlongsideTheDiagonalRoadItJoins()
    {
        var (game, king) = Blank();
        var track = game.Map.Content.Roads.Get(DefaultRoads.TrackName);
        for (int i = 0; i <= 8; i++)
        {
            for (int x = 10 + 3 * i; x <= 13 + 3 * i; x++) game.Map.SetRoad(x, 10 + i, king);
        }

        for (int y = 5; y <= 12; y++) game.Map.SetRoad(21, y, track);
        var paths = Paths(game);
        var highway = Assert.Single(paths, p => p.Type == king);
        var branch = Assert.Single(paths, p => p.Type == track);

        int n = branch.Count;
        double endX = branch.Xs[n - 1], endY = branch.Ys[n - 1];
        double nearest = double.PositiveInfinity;
        double tx = 0, ty = 0;
        for (int i = 0; i + 1 < highway.Count; i++)
        {
            double d = Math.Sqrt(Math.Pow(highway.Xs[i] - endX, 2) + Math.Pow(highway.Ys[i] - endY, 2));
            if (d < nearest)
            {
                nearest = d;
                tx = highway.Xs[i + 1] - highway.Xs[i];
                ty = highway.Ys[i + 1] - highway.Ys[i];
            }
        }

        Assert.True(nearest < 12, $"the branch should end on the highway, not {nearest:F1}px from it");
        double bx = endX - branch.Xs[n - 6], by = endY - branch.Ys[n - 6];
        double cos = Math.Abs(bx * tx + by * ty) / (Math.Sqrt(bx * bx + by * by) * Math.Sqrt(tx * tx + ty * ty));
        Assert.True(cos > 0.8, $"the branch should run into the highway's direction (cos {cos:F2})");
    }

    [Fact]
    public void RingIsOneClosedPath()
    {
        var (game, king) = Blank();
        for (int x = 5; x <= 12; x++) { game.Map.SetRoad(x, 5, king); game.Map.SetRoad(x, 12, king); }
        for (int y = 5; y <= 12; y++) { game.Map.SetRoad(5, y, king); game.Map.SetRoad(12, y, king); }
        var path = Assert.Single(Paths(game));
        Assert.True(path.Closed);
    }

    [Fact]
    public void LoneCellIsAPoint()
    {
        var (game, king) = Blank();
        game.Map.SetRoad(9, 9, king);
        var path = Assert.Single(Paths(game));
        Assert.True(path.Count >= 1);
    }

    [Fact]
    public void TrimShortensAnEndWithoutLosingTheShape()
    {
        double[] xs = [0, 4, 8, 12], ys = [0, 0, 0, 0];
        var (tx, _) = RoadCurves.Trim(xs, ys, atStart: false, 5);
        Assert.Equal(7, tx[^1], 6);
        var (sx, _) = RoadCurves.Trim(xs, ys, atStart: true, 5);
        Assert.Equal(5, sx[0], 6);
        var (px, _) = RoadCurves.Trim(xs, ys, atStart: false, 100);
        Assert.Single(px);
    }

    private static byte[] Pixel(RoadChunk chunk, double px, double py, int scale = 1) =>
        chunk.Rgba.AsSpan((((int)Math.Floor(py * scale)) * chunk.Width + (int)Math.Floor(px * scale)) * 4, 4).ToArray();

    [Theory(Skip = LegacyPaintedRoad)]
    [InlineData(1)]
    [InlineData(2)]
    public void RightHandCobbledBranchKeepsLeftHandHighwayPavement(int scale)
    {
        var (game, king) = Blank();
        var cobbled = game.Map.Content.Roads.Get(DefaultRoads.CobbledName);
        for (int y = 3; y <= 18; y++) game.Map.SetRoad(10, y, king);
        for (int x = 0; x <= 10; x++) game.Map.SetRoad(x, 10, king);
        for (int x = 11; x <= 15; x++) game.Map.SetRoad(x, 10, cobbled);
        var layer = new RoadVectorLayer();
        layer.Sync(game);
        var chunk = layer.Render(0, 0, scale)!;
        for (int x = 6; x <= 8; x++)
            Assert.Equal(new byte[] { king.Background.R, king.Background.G, king.Background.B, 255 },
                Pixel(chunk, CentreX(x), CentreY(10), scale));
        Assert.Equal(new byte[] { cobbled.Background.R, cobbled.Background.G, cobbled.Background.B, 255 },
            Pixel(chunk, CentreX(14), CentreY(10), scale));
    }

    [Theory(Skip = LegacyPaintedRoad)]
    [InlineData(1)]
    [InlineData(2)]
    public void LeftHandBranchCannotPaintTheOppositeHighwayEdge(int scale)
    {
        var (game, king) = Blank();
        var cobbled = game.Map.Content.Roads.Get(DefaultRoads.CobbledName);
        for (int y = 0; y <= 18; y++) game.Map.SetRoad(10, y, king);
        for (int x = 10; x <= 20; x++) game.Map.SetRoad(x, 10, king);
        for (int x = 3; x < 10; x++) game.Map.SetRoad(x, 10, cobbled);
        var layer = new RoadVectorLayer();
        layer.Sync(game);
        var chunk = layer.Render(0, 0, scale)!;
        int checkedPixels = 0;
        for (int x = (int)(CentreX(10) + 2) * scale; x < 15 * W * scale; x++)
            for (int y = (int)(CentreY(10) - 12) * scale; y < (CentreY(10) + 12) * scale; y++)
            {
                var pixel = chunk.Rgba.AsSpan((y * chunk.Width + x) * 4, 4);
                if (pixel[3] <= 32) continue;
                Assert.True(pixel[0] >= pixel[2],
                    $"Minor-road gray/blue leaked onto the opposite highway at ({x}, {y}): {pixel[0]},{pixel[1]},{pixel[2]}.");
                checkedPixels++;
            }
        Assert.True(checkedPixels > 100, "Regression must cover the highway bed and both outer lines.");
    }

    [Theory(Skip = LegacyPaintedRoad)]
    [InlineData(1)]
    [InlineData(2)]
    public void BranchRampDoesNotRecolourTheHighwayItOverlaps(int scale)
    {
        var (game, king) = Blank();
        var track = game.Map.Content.Roads.Get(DefaultRoads.TrackName);
        for (int i = 0; i <= 8; i++)
            for (int x = 10 + 3 * i; x <= 13 + 3 * i; x++)
                game.Map.SetRoad(x, 10 + i, king);
        for (int y = 5; y <= 12; y++) game.Map.SetRoad(21, y, track);
        var layer = new RoadVectorLayer();
        layer.Sync(game);
        var highway = Assert.Single(layer.Paths, path => path.Type == king);
        var branch = Assert.Single(layer.Paths, path => path.Type == track);
        int checkedPixels = 0;
        foreach (int i in Enumerable.Range(0, branch.Count))
        {
            double x = branch.Xs[i], y = branch.Ys[i];
            double distance = Enumerable.Range(0, highway.Count)
                .Min(j => Math.Sqrt(Math.Pow(highway.Xs[j] - x, 2) + Math.Pow(highway.Ys[j] - y, 2)));
            if (distance > 2) continue;
            int cx = (int)(x / (RoadVectorLayer.ChunkCells * W));
            int cy = (int)(y / (RoadVectorLayer.ChunkCells * H));
            var chunk = layer.Render(cx, cy, scale)!;
            var pixel = Pixel(chunk, x - cx * RoadVectorLayer.ChunkCells * W,
                y - cy * RoadVectorLayer.ChunkCells * H, scale);
            Assert.Equal(new byte[] { king.Background.R, king.Background.G, king.Background.B, 255 }, pixel);
            checkedPixels++;
        }
        Assert.True(checkedPixels >= 3, "Regression must sample the ramp where it overlaps the highway.");
    }

    [Theory(Skip = LegacyPaintedRoad)]
    [InlineData(DefaultRoads.TrackName, 1)]
    [InlineData(DefaultRoads.TrackName, 2)]
    [InlineData(DefaultRoads.CobbledName, 1)]
    [InlineData(DefaultRoads.CobbledName, 2)]
    [InlineData(DefaultRoads.KingsRoadName, 1)]
    [InlineData(DefaultRoads.KingsRoadName, 2)]
    [InlineData(DefaultRoads.KingsRoadName, 1, 2)]
    [InlineData(DefaultRoads.KingsRoadName, 1, 4)]
    public void EveryLocalRoadRetainsItsNormalColors(string name, int scale, int stride = 1)
    {
        var (game, _) = Blank();
        var type = game.Map.Content.Roads.Get(name);
        for (int x = 5; x <= 14; x++) game.Map.SetRoad(x, 5, type);
        game.Touch();
        var glyph = CellRenderer.Render(game, 8, 5);
        Assert.Equal(type.Foreground, glyph.Foreground);

        var layer = new RoadVectorLayer();
        layer.Sync(game);
        Assert.All(layer.Paths, path => Assert.False(path.Connected));
        var chunk = layer.Render(0, 0, scale, stride)!;
        double offset = stride == 1 ? RoadVectorLayer.LineOffset : RoadVectorLayer.HighwayLineOffset;
        var line = Pixel(chunk, CentreX(8) / stride, CentreY(5) / stride - offset, scale);
        var localLine = line;

        for (int x = 0; x < 5; x++) game.Map.SetRoad(x, 5, type);
        game.Touch();
        Assert.Equal(type.Foreground, CellRenderer.Render(game, 8, 5).Foreground);
        layer.Sync(game);
        Assert.All(layer.Paths, path => Assert.True(path.Connected));
        var connectedChunk = layer.Render(0, 0, scale, stride)!;
        var connectedLine = Pixel(connectedChunk, CentreX(8) / stride, CentreY(5) / stride - offset, scale);
        Assert.Equal(connectedLine, localLine);
    }

    [Fact(Skip = LegacyPaintedRoad)]
    public void RoadBedAndEdgeLinesAreDrawn()
    {
        var (game, king) = Blank();
        for (int x = 0; x < 16; x++) game.Map.SetRoad(x, 5, king);
        var layer = new RoadVectorLayer();
        layer.Sync(game);
        var chunk = layer.Render(0, 0, 1);
        Assert.NotNull(chunk);

        double cy = CentreY(5);
        var middle = Pixel(chunk, 90, cy);
        Assert.Equal(255, middle[3]);
        Assert.Equal(king.Background.R, middle[0]);
        var line = Pixel(chunk, 90, cy - RoadVectorLayer.LineOffset);
        Assert.InRange(line[3], 200, 255);
        Assert.InRange(Math.Abs(line[0] - king.Foreground.R), 0, 40);
        Assert.True(Math.Abs(line[0] - king.Foreground.R) < Math.Abs(line[0] - king.Background.R) || king.Foreground.R == king.Background.R);
        Assert.Equal(0, Pixel(chunk, 90, cy - RoadVectorLayer.BedHalfWidth - 3)[3]);
    }

    [Fact(Skip = LegacyPaintedRoad)]
    public void ConcaveCornersGetFillets()
    {
        var (game, king) = Blank();
        for (int x = 0; x < 16; x++) game.Map.SetRoad(x, 5, king);
        for (int y = 5; y < 16; y++) game.Map.SetRoad(8, y, king);
        var layer = new RoadVectorLayer();
        layer.Sync(game);
        var chunk = layer.Render(0, 0, 1)!;
        double cx = CentreX(8), cy = CentreY(5);
        double half = RoadVectorLayer.BedHalfWidth;
        // A hard union of the two beds has its inner corner at (+-half, +half); the blend fills in beyond it.
        double diag = 1.0;
        Assert.True(Pixel(chunk, cx + half + diag, cy + half + diag)[3] > 128);
    }

    [Fact(Skip = LegacyPaintedRoad)]
    public void EmptyChunksAndPlainRoadsRenderNothing()
    {
        var (game, king) = Blank();
        game.Map.SetRoad(2, 2, king);
        var layer = new RoadVectorLayer();
        layer.Sync(game);
        Assert.Null(layer.Render(3, 3, 1));
        Assert.NotNull(layer.Render(0, 0, 1));
        Assert.NotNull(layer.Render(0, 0, 2));
    }

    [Fact]
    public void RevisionChangesOnlyWhenRoadsDo()
    {
        var (game, king) = Blank();
        game.Map.SetRoad(2, 2, king);
        var layer = new RoadVectorLayer();
        Assert.True(layer.Sync(game));
        int revision = layer.Revision;
        Assert.False(layer.Sync(game));
        game.Map.SetRoad(3, 2, king);
        game.Touch();
        Assert.True(layer.Sync(game));
        Assert.True(layer.Revision > revision);
    }

    [Fact(Skip = LegacyPaintedRoad)]
    public void PixelsOutsideTheMapAreTransparent()
    {
        var (game, king) = Blank();
        for (int y = 0; y < 16; y++) game.Map.SetRoad(0, y, king);
        var layer = new RoadVectorLayer();
        layer.Sync(game);
        Assert.Null(layer.Render(-1, 0, 1));
    }

    [Fact(Skip = LegacyPaintedRoad)]
    public void RoadsOverWaterStayOpaqueBetweenTheLines()
    {
        var (game, king) = Blank();
        var water = game.Map.Content.Terrains.Where(t => !t.Buildable).First();
        for (int x = 0; x < 16; x++)
        {
            game.Map.SetTerrain(x, 5, water);
            game.Map.SetRoad(x, 5, king);
        }

        var layer = new RoadVectorLayer();
        layer.Sync(game);
        var chunk = layer.Render(0, 0, 1)!;
        double cy = CentreY(5);
        Assert.Equal(255, Pixel(chunk, 90, cy)[3]);
        Assert.Equal(255, Pixel(chunk, 90, cy + 2.5)[3]);
        Assert.InRange(Pixel(chunk, 90, cy - RoadVectorLayer.LineOffset)[3], 200, 255);
    }

    [Fact(Skip = LegacyPaintedRoad)]
    public void TheBedEndsAtTheOuterEdgeOfTheLines()
    {
        Assert.Equal(RoadVectorLayer.LineOffset + RoadVectorLayer.LineHalfThickness, RoadVectorLayer.BedHalfWidth, 9);
        var (game, king) = Blank();
        for (int x = 0; x < 16; x++) game.Map.SetRoad(x, 5, king);
        var layer = new RoadVectorLayer();
        layer.Sync(game);
        var chunk = layer.Render(0, 0, 1)!;
        double cy = CentreY(5);
        Assert.Equal(0, Pixel(chunk, 90, cy - RoadVectorLayer.BedHalfWidth - 1.5)[3]);
        Assert.Equal(0, Pixel(chunk, 90, cy + RoadVectorLayer.BedHalfWidth + 1.5)[3]);
    }

    [Fact(Skip = LegacyPaintedRoad)]
    public void AngledStairsDrawNoTerrainThroughTheRoad()
    {
        var (game, king) = Blank();
        for (int i = 0; i < 10; i++)
        {
            game.Map.SetRoad(2 + i, 2 + i, king);
            game.Map.SetRoad(3 + i, 2 + i, king);
        }

        var layer = new RoadVectorLayer();
        layer.Sync(game);
        var chunk = layer.Render(0, 0, 1)!;
        var path = Assert.Single(layer.Paths);
        // Every point along the drawn curve is covered by an opaque bed.
        for (int i = 4; i < path.Count - 4; i += 3)
        {
            Assert.Equal(255, Pixel(chunk, path.Xs[i], path.Ys[i])[3]);
        }
    }

    [Fact(Skip = LegacyPaintedRoad)]
    public void ZoomedOutChunksDrawThinnerFainterRoads()
    {
        var (game, king) = Blank();
        for (int x = 0; x < 64; x++) game.Map.SetRoad(x, 8, king);
        var layer = new RoadVectorLayer();
        layer.Sync(game);
        const int stride = 4;
        var chunk = layer.Render(0, 0, 1, stride)!;
        Assert.Equal(RoadVectorLayer.ChunkCells * W, chunk.Width);
        Assert.Equal(RoadVectorLayer.ChunkCells * H, chunk.Height);
        double half = RoadVectorLayer.HighwayBedHalf;
        Assert.True(half < RoadVectorLayer.BedHalfWidth * 0.6);
        // Row 8 of the map is in the screen row 2 (rows 8..11 are one block); its road is drawn thin and translucent.
        double y = CentreY(8) / stride;
        int alpha = Pixel(chunk, 90, y)[3];
        Assert.InRange(alpha, 100, 254);
        Assert.Equal(0, Pixel(chunk, 90, y + half + 2)[3]);
        Assert.Equal(0, Pixel(chunk, 90, y - half - 2)[3]);

        // The highway keeps a thin double line in its own yellow, one either side of the middle.
        bool Yellow(double at) => Pixel(chunk, 90, at) is { } p && p[0] > 200 && p[2] < 170 && p[3] > 150;
        Assert.True(Yellow(y + RoadVectorLayer.HighwayLineOffset) || Yellow(y + RoadVectorLayer.HighwayLineOffset + 0.5));
        Assert.True(Yellow(y - RoadVectorLayer.HighwayLineOffset) || Yellow(y - RoadVectorLayer.HighwayLineOffset - 0.5));
        Assert.False(Yellow(y));
    }

    [Fact(Skip = LegacyPaintedRoad)]
    public void RoadsThinOutAsTheMapZoomsOut()
    {
        Assert.Equal(1.0, RoadVectorLayer.Weight(1));
        Assert.Equal(1.0, RoadVectorLayer.Opacity(1));
        for (int stride = 2; stride <= 16; stride *= 2)
        {
            Assert.True(stride > 8 || RoadVectorLayer.Weight(stride) < RoadVectorLayer.Weight(stride / 2));
            Assert.True(RoadVectorLayer.Weight(stride) <= RoadVectorLayer.Weight(stride / 2));
            Assert.True(RoadVectorLayer.Opacity(stride) < RoadVectorLayer.Opacity(stride / 2));
        }

        Assert.True(RoadVectorLayer.Weight(4) <= 0.25);
        Assert.True(RoadVectorLayer.Shown(1, 4, 4));
        Assert.False(RoadVectorLayer.Shown(1, 4, 16));
        Assert.True(RoadVectorLayer.Shown(4, 4, 16));
    }

    [Fact]
    public void GuideSnapsToTheDrawnCurveAndIgnoresDistantPoints()
    {
        var (game, king) = Blank();
        for (int i = 0; i < 12; i++)
        {
            game.Map.SetRoad(4 + i, 2 + i, king);
            game.Map.SetRoad(5 + i, 2 + i, king);
        }

        var layer = new RoadVectorLayer();
        layer.Sync(game);
        var guide = new RoadGuide(layer.Paths);
        var path = Assert.Single(layer.Paths);
        int mid = path.Count / 2;
        Assert.True(guide.TrySnap(path.Xs[mid] + 5, path.Ys[mid] + 3, out double sx, out double sy));
        double off = Math.Abs((path.Xs[^1] - path.Xs[0]) * (sy - path.Ys[0]) - (path.Ys[^1] - path.Ys[0]) * (sx - path.Xs[0])) /
            Math.Sqrt(Math.Pow(path.Xs[^1] - path.Xs[0], 2) + Math.Pow(path.Ys[^1] - path.Ys[0], 2));
        Assert.True(off < 3, $"snapped point is {off:0.0}px off the line");
        Assert.False(guide.TrySnap(40 * W, 40 * H, out _, out _));
        Assert.True(new RoadGuide([]).IsEmpty);
    }

    [Fact(Skip = "Legacy glyph-map blanking is not a dedicated-mesh rendering contract.")]
    public void CellRendererLeavesVectorRoadCellsBlank()
    {
        var (game, king) = Blank();
        game.Map.SetRoad(4, 4, king);
        game.Map.SetRoad(5, 4, king);
        var glyphs = CellRenderer.Render(game, 4, 4);
        var vector = CellRenderer.Render(game, 4, 4, vectorRoads: true);
        Assert.NotEqual(" ", glyphs.Glyph);
        Assert.Equal(" ", vector.Glyph);
        Assert.Equal(game.Map.TerrainAt(4, 4).Background, vector.Background);
        game.Map.SetRoad(7, 7, true);
        Assert.Equal(" ", CellRenderer.Render(game, 7, 7, vectorRoads: true).Glyph);
    }
}
