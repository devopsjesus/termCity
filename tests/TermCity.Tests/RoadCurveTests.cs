using TermCity.Core.Rendering;
using TermCity.Core.Roads;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Tests;

public class RoadCurveTests
{
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
            Assert.All(path.Ys, y => Assert.Equal(CentreY(10), y, 6));
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
        Assert.All(a.Ys.Concat(b.Ys), y => Assert.Equal(CentreY(10), y, 6));
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
        Assert.All(path.Ys, y => Assert.Equal(CentreY(10), y, 6));
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

        // The corner cell's centre is cut off, not visited.
        double nearest = Enumerable.Range(0, path.Count).Min(i => Math.Abs(path.Xs[i] - CentreX(20)) + Math.Abs(path.Ys[i] - CentreY(10)));
        Assert.True(nearest > 4);
        Assert.True(nearest < W + H);
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
        Assert.Contains(paths, p => p.Ys.All(y => Math.Abs(y - CentreY(10)) < 1e-6));
        Assert.Contains(paths, p => p.Xs.All(x => Math.Abs(x - CentreX(10)) < 1e-6));
    }

    [Fact]
    public void BranchStopsShortOfTheRoadItJoins()
    {
        var (game, king) = Blank();
        for (int x = 5; x <= 20; x++) game.Map.SetRoad(x, 10, king);
        for (int y = 11; y <= 18; y++) game.Map.SetRoad(12, y, king);
        var paths = Paths(game);
        Assert.Equal(2, paths.Count);
        var stem = paths.Single(p => p.Xs.All(x => Math.Abs(x - CentreX(12)) < 1e-6));
        double top = stem.Ys.Min();
        Assert.Equal(CentreY(10) + RoadCurves.JunctionSetback, top, 6);
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

    [Theory]
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

    [Theory]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public void PixelsOutsideTheMapAreTransparent()
    {
        var (game, king) = Blank();
        for (int y = 0; y < 16; y++) game.Map.SetRoad(0, y, king);
        var layer = new RoadVectorLayer();
        layer.Sync(game);
        Assert.Null(layer.Render(-1, 0, 1));
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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
