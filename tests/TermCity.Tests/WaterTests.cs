using TermCity.Core.Simulation;
using TermCity.Core.Terrain;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class WaterTests
{
    private static (GameMap Map, WaterPlan Plan) Build(int seed, int w = 160, int h = 96)
    {
        var content = new GameContent();
        var map = new GameMap(w, h, content);
        var plan = WaterGenerator.Build(map, content.Terrains.Get("Water"), seed);
        return (map, plan);
    }

    private static bool IsWater(GameMap map, int x, int y) => !map.TerrainAt(x, y).Buildable;

    private static int EdgeDistance(GameMap map, int x, int y) => Math.Min(Math.Min(x, map.Width - 1 - x), Math.Min(y, map.Height - 1 - y));

    private static IEnumerable<(int Seed, int W, int H)> Maps()
    {
        for (int seed = 1; seed <= 150; seed++)
        {
            yield return (seed, 160, 96);
        }

        for (int seed = 1; seed <= 20; seed++)
        {
            yield return (seed, 320, 192);
        }

        for (int seed = 1; seed <= 6; seed++)
        {
            yield return (seed, 640, 384);
        }
    }

    [Fact]
    public void EveryKindOfMapAppearsAndNotEveryMapHasASea()
    {
        var types = new Dictionary<WaterMapType, int>();
        int withSea = 0, total = 0;
        for (int seed = 1; seed <= 150; seed++)
        {
            var plan = Build(seed).Plan;
            types[plan.Type] = types.GetValueOrDefault(plan.Type) + 1;
            withSea += plan.Bodies.Any(b => b.Kind == WaterKind.Sea) ? 1 : 0;
            total++;
        }

        foreach (var type in Enum.GetValues<WaterMapType>())
        {
            Assert.True(types.GetValueOrDefault(type) >= 6, $"{type}: {types.GetValueOrDefault(type)} of {total}");
        }

        Assert.InRange(withSea, total * 25 / 100, total * 65 / 100);
    }

    [Fact]
    public void ASeaCoversAtMostThirtyPercentOfItsEdgeAndReachesAtMostTenCellsIn()
    {
        int seas = 0;
        foreach (var (seed, w, h) in Maps())
        {
            var (map, plan) = Build(seed, w, h);
            foreach (var sea in plan.Bodies.Where(b => b.Kind == WaterKind.Sea))
            {
                seas++;
                bool alongX = sea.Side is MapSide.North or MapSide.South;
                int edge = alongX ? w : h;
                Assert.InRange(sea.Depth, 5, 10);
                Assert.True(sea.Span <= edge * WaterGenerator.MaxSeaShareOfEdge + 1, $"seed {seed} {w}x{h}: span {sea.Span} of {edge}");

                // The water that was actually made agrees with the plan.
                int minAlong = int.MaxValue, maxAlong = int.MinValue, deepest = 0;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        if (plan.IdAt(x, y) != sea.Id)
                        {
                            continue;
                        }

                        int along = alongX ? x : y;
                        minAlong = Math.Min(minAlong, along);
                        maxAlong = Math.Max(maxAlong, along);
                        int inward = sea.Side switch
                        {
                            MapSide.North => y + 1,
                            MapSide.South => h - y,
                            MapSide.West => x + 1,
                            _ => w - x,
                        };
                        deepest = Math.Max(deepest, inward);
                    }
                }

                Assert.True(deepest <= 10, $"seed {seed} {w}x{h}: the sea reaches {deepest} cells in");
                Assert.True(maxAlong - minAlong + 1 <= edge * WaterGenerator.MaxSeaShareOfEdge + 2, $"seed {seed} {w}x{h}: the sea runs {maxAlong - minAlong + 1} of {edge}");
            }
        }

        Assert.True(seas > 40, $"only {seas} seas");
    }

    [Fact]
    public void WaterThatTouchesAnEdgeNeverReachesMoreThanTenCellsFromIt()
    {
        foreach (var (seed, w, h) in Maps())
        {
            var (map, plan) = Build(seed, w, h);
            foreach (var body in plan.Bodies.Where(b => b.Kind is WaterKind.Sea or WaterKind.EdgeLake or WaterKind.CornerLake))
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        // River cells are carved over the lake, so only look at the lake's own water.
                        if (plan.IdAt(x, y) == body.Id)
                        {
                            Assert.True(EdgeDistance(map, x, y) <= 10, $"seed {seed} {w}x{h}: {body.Kind} water {EdgeDistance(map, x, y) + 1} cells from the edge at {x},{y}");
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void RiversNeverTouchOtherWaterExceptTheBodyTheyFeedAndMeetOnlyInAConfluence()
    {
        foreach (var (seed, w, h) in Maps())
        {
            var (map, plan) = Build(seed, w, h);
            var target = plan.Rivers.ToDictionary(r => r.Id, r => r.TargetBodyId);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int a = plan.IdAt(x, y);
                    if (a == 0)
                    {
                        continue;
                    }

                    foreach (var (dx, dy) in new[] { (1, 0), (0, 1) })
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= w || ny >= h)
                        {
                            continue;
                        }

                        int b = plan.IdAt(nx, ny);
                        if (b == 0 || a == b)
                        {
                            continue;
                        }

                        bool riverIntoItsBody = (a >= WaterPlan.FirstRiverId && target[a] == b) || (b >= WaterPlan.FirstRiverId && target[b] == a);
                        Assert.True(riverIntoItsBody, $"seed {seed} {w}x{h}: water {a} touches water {b} at {x},{y}");
                    }
                }
            }

            // Only a confluence map has a river made of two joined rivers.
            foreach (var river in plan.Rivers.Where(r => r.Sources == 2))
            {
                Assert.Equal(WaterMapType.RiverConfluence, plan.Type);
            }
        }
    }

    [Fact]
    public void ConfluenceMapsReallyHaveTwoRiversJoiningIntoOne()
    {
        int confluences = 0;
        foreach (var (seed, w, h) in Maps().Where(m => m.W == 160))
        {
            var (map, plan) = Build(seed, w, h);
            if (plan.Type != WaterMapType.RiverConfluence)
            {
                continue;
            }

            var river = plan.Rivers.FirstOrDefault(r => r.Sources == 2);
            if (river is null)
            {
                continue;
            }

            // Count the places this one river touches the edge of the map: its two sources.
            var edgePoints = new List<(int X, int Y)>();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (plan.IdAt(x, y) == river.Id && (x == 0 || y == 0 || x == w - 1 || y == h - 1))
                    {
                        edgePoints.Add((x, y));
                    }
                }
            }

            // Two separate groups of edge cells at least a few cells apart.
            bool twoSources = edgePoints.Any(p => edgePoints.Any(q => Math.Abs(p.X - q.X) + Math.Abs(p.Y - q.Y) > 12));
            Assert.True(twoSources, $"seed {seed}: the confluence has only one source on the edge");
            confluences++;
        }

        Assert.True(confluences >= 8, $"only {confluences} confluences");
    }

    [Fact]
    public void RiversStartAtTheMapEdgeAndFlowIntoTheirBody()
    {
        int rivers = 0;
        foreach (var (seed, w, h) in Maps())
        {
            var (map, plan) = Build(seed, w, h);
            foreach (var river in plan.Rivers)
            {
                rivers++;
                bool onEdge = false, meetsBody = false;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        if (plan.IdAt(x, y) != river.Id)
                        {
                            continue;
                        }

                        onEdge |= x == 0 || y == 0 || x == w - 1 || y == h - 1;
                        foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                        {
                            int nx = x + dx, ny = y + dy;
                            meetsBody |= map.InBounds(nx, ny) && plan.IdAt(nx, ny) == river.TargetBodyId;
                        }
                    }
                }

                Assert.True(onEdge, $"seed {seed} {w}x{h}: river {river.Id} does not start on the edge of the map");
                Assert.True(meetsBody, $"seed {seed} {w}x{h}: river {river.Id} does not reach body {river.TargetBodyId}");
            }
        }

        Assert.True(rivers > 100, $"only {rivers} rivers");
    }

    [Fact]
    public void MostBodiesAreFedByARiverButNotAll()
    {
        int fed = 0, unfed = 0, unfedEdge = 0;
        foreach (var (seed, w, h) in Maps().Where(m => m.W == 160))
        {
            var plan = Build(seed, w, h).Plan;
            foreach (var body in plan.Bodies)
            {
                if (body.Fed)
                {
                    fed++;
                }
                else
                {
                    unfed++;
                    unfedEdge += body.Kind is WaterKind.Sea or WaterKind.EdgeLake or WaterKind.CornerLake ? 1 : 0;
                }
            }
        }

        Assert.True(fed > unfed * 2, $"fed {fed}, unfed {unfed}");
        Assert.True(unfed > 10 && unfedEdge > 3, $"unfed {unfed} (on an edge or in a corner: {unfedEdge})");
    }

    [Fact]
    public void BodiesOfWaterAreSeparateFromOneAnother()
    {
        foreach (var (seed, w, h) in Maps().Where(m => m.W <= 320))
        {
            var plan = Build(seed, w, h).Plan;
            Assert.All(plan.Bodies, b => Assert.True(b.Id < WaterPlan.FirstRiverId));
            Assert.Equal(plan.Bodies.Count, plan.Bodies.Select(b => b.Id).Distinct().Count());
        }
    }

    [Fact]
    public void TotalWaterIsAModestShareOfTheMap()
    {
        foreach (var (seed, w, h) in Maps())
        {
            var map = Build(seed, w, h).Map;
            int water = 0;
            for (int i = 0; i < map.Width * map.Height; i++)
            {
                water += IsWater(map, i % map.Width, i / map.Width) ? 1 : 0;
            }

            double share = water / (double)(map.Width * map.Height);
            Assert.InRange(share, 0.005, 0.30);
        }
    }

    [Fact]
    public void EachEdgeIsMostlyLand()
    {
        foreach (var (seed, w, h) in Maps())
        {
            var map = Build(seed, w, h).Map;
            double Share(IEnumerable<(int X, int Y)> cells) => cells.Count(c => IsWater(map, c.X, c.Y)) / (double)cells.Count();

            Assert.True(Share(Enumerable.Range(0, w).Select(x => (x, 0))) <= 0.5, $"seed {seed} {w}x{h}: north edge");
            Assert.True(Share(Enumerable.Range(0, w).Select(x => (x, h - 1))) <= 0.5, $"seed {seed} {w}x{h}: south edge");
            Assert.True(Share(Enumerable.Range(0, h).Select(y => (0, y))) <= 0.5, $"seed {seed} {w}x{h}: west edge");
            Assert.True(Share(Enumerable.Range(0, h).Select(y => (w - 1, y))) <= 0.5, $"seed {seed} {w}x{h}: east edge");
        }
    }

    [Fact]
    public void LargeLakeMapsHaveABigLakeInsideTheMap()
    {
        int large = 0;
        foreach (var (seed, w, h) in Maps().Where(m => m.W == 160))
        {
            var plan = Build(seed, w, h).Plan;
            if (plan.Type != WaterMapType.LargeLake)
            {
                continue;
            }

            var lake = plan.Bodies.First();
            Assert.Equal(WaterKind.InlandLake, lake.Kind);
            Assert.True(lake.Span >= 30, $"seed {seed}: lake only {lake.Span} wide");
            large++;
        }

        Assert.True(large >= 10);
    }

    [Fact]
    public void ChangingTheMapOrTheSeedChangesTheWaterButTheSameSeedGivesTheSame()
    {
        var a = Build(11).Map;
        var b = Build(11).Map;
        Assert.Equal(a.TerrainLayer, b.TerrainLayer);
        Assert.NotEqual(a.TerrainLayer, Build(12).Map.TerrainLayer);

        // The water in a full game is the same water.
        var game = CityGame.New(new GameConfig { Seed = 11 });
        for (int i = 0; i < a.TerrainLayer.Length; i++)
        {
            Assert.Equal(IsWater(a, i % a.Width, i / a.Width), IsWater(game.Map, i % a.Width, i / a.Width));
        }
    }
}
