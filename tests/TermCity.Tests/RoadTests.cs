using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Roads;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace TermCity.Tests;

public class RoadTypeTests
{
    private static RoadType Type(CityGame g, string name) => g.Map.Content.Roads.Get(name);

    [Fact]
    public void StreetsAvenuesAndHighwaysCostMoreInThatOrder()
    {
        var game = TestCity.Flat();
        Assert.Equal(500, game.RoadCostAt(1, 1, Type(game, DefaultRoads.TrackName)));
        Assert.Equal(900, game.RoadCostAt(1, 1, Type(game, DefaultRoads.CobbledName)));
        Assert.Equal(1500, game.RoadCostAt(1, 1, Type(game, DefaultRoads.KingsRoadName)));
        Assert.Equal(500, game.RoadCostAt(1, 1));
    }

    [Fact]
    public void BuildingARoadStoresItsType()
    {
        var game = TestCity.Flat();
        var avenue = Type(game, DefaultRoads.CobbledName);
        var result = game.BuildRoad(new CellRect(5, 5, 3, 1), avenue);
        Assert.True(result.Success, result.Message);
        Assert.Equal(3 * 900, result.Cost);
        Assert.Equal(avenue, game.Map.RoadTypeAt(6, 5));
        Assert.Equal(50_000 - 2700, game.Money);
    }

    [Fact]
    public void UpgradingPaysOnlyTheDifferenceAndNeverDowngrades()
    {
        var game = TestCity.Flat();
        var street = Type(game, DefaultRoads.TrackName);
        var highway = Type(game, DefaultRoads.KingsRoadName);

        // Row 20 is a street already: a highway over it costs 1500 - 500 per cell.
        var quote = game.QuoteRoad(new CellRect(10, 20, 4, 1), highway);
        Assert.Equal(4, quote.Cells);
        Assert.Equal(4 * 1000, quote.Cost);
        Assert.True(game.BuildRoad(new CellRect(10, 20, 4, 1), highway).Success);
        Assert.Equal(highway, game.Map.RoadTypeAt(11, 20));

        // The same type, or a smaller one, over an existing road is not allowed.
        Assert.Equal(0, game.QuoteRoad(new CellRect(10, 20, 4, 1), highway).Cells);
        Assert.Equal(0, game.QuoteRoad(new CellRect(10, 20, 4, 1), street).Cells);
        Assert.Equal(highway, game.Map.RoadTypeAt(11, 20));
    }

    [Fact]
    public void ARoadWithoutATypeIsAStreet()
    {
        var game = TestCity.Flat();
        game.Map.SetRoad(7, 7, true);
        Assert.Equal(DefaultRoads.TrackName, game.Map.RoadTypeAt(7, 7)!.Name);
        game.Map.SetRoad(7, 7, false);
        Assert.Null(game.Map.RoadTypeAt(7, 7));
    }

    [Fact]
    public void EachTypeDrawsWithItsOwnGlyphsAndJunctionsConnect()
    {
        var game = TestCity.Flat();
        var map = game.Map;
        var highway = Type(game, DefaultRoads.KingsRoadName);
        var avenue = Type(game, DefaultRoads.CobbledName);

        // A plus-shaped highway junction, and an avenue stretch.
        foreach (var (x, y) in new[] { (30, 10), (29, 10), (31, 10), (30, 9), (30, 11) })
        {
            map.SetRoad(x, y, highway);
        }

        foreach (int x in new[] { 40, 41, 42 })
        {
            map.SetRoad(x, 10, avenue);
        }

        game.Touch();
        Assert.Equal("╬", CellRenderer.Render(game, 30, 10).Glyph);
        Assert.Equal("═", CellRenderer.Render(game, 29, 10).Glyph);
        Assert.Equal("║", CellRenderer.Render(game, 30, 9).Glyph);
        Assert.Equal("━", CellRenderer.Render(game, 41, 10).Glyph);
        Assert.Equal("─", CellRenderer.Render(game, 5, 20).Glyph);
    }

    [Fact]
    public void RoadsOverWaterAreDrawnAsBridgesOnTheWater()
    {
        var game = TestCity.Flat();
        var water = game.Map.Content.Terrains.Get("Water");
        game.Map.SetTerrain(50, 20, water);
        game.Touch();
        var bridge = CellRenderer.Render(game, 50, 20);
        Assert.Equal(water.Background, bridge.Background);
        Assert.Equal("─", bridge.Glyph);
    }

    [Fact]
    public void RoadTypesRoundTripThroughSaves()
    {
        var game = TestCity.Flat();
        game.BuildRoad(new CellRect(5, 5, 4, 1), Type(game, DefaultRoads.CobbledName));
        game.BuildRoad(new CellRect(5, 6, 4, 1), Type(game, DefaultRoads.KingsRoadName));
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(game.Map.RoadTypeLayer, loaded.Map.RoadTypeLayer);
        Assert.Equal(DefaultRoads.CobbledName, loaded.Map.RoadTypeAt(6, 5)!.Name);
        Assert.Equal(DefaultRoads.KingsRoadName, loaded.Map.RoadTypeAt(6, 6)!.Name);
        Assert.Equal(DefaultRoads.TrackName, loaded.Map.RoadTypeAt(6, 20)!.Name);
    }

    [Fact]
    public void SavesFromBeforeRoadTypesLoadWithEveryRoadAStreet()
    {
        var game = TestCity.Flat();
        game.BuildRoad(new CellRect(5, 5, 4, 1), Type(game, DefaultRoads.KingsRoadName));
        var root = JsonNode.Parse(SaveGameStore.Serialize(game))!.AsObject();
        root.Remove("RoadTypes");

        var loaded = SaveGameStore.Deserialize(root.ToJsonString());
        Assert.Equal(game.Map.RoadCount, loaded.Map.RoadCount);
        Assert.All(loaded.Map.RoadCells, i => Assert.Equal(DefaultRoads.TrackName, loaded.Map.RoadTypeAt(i % loaded.Map.Width, i / loaded.Map.Width)!.Name));
    }
}

public class HighwayGenerationTests
{
    private static CityGame Generate(int w, int h, int seed) => CityGame.New(new GameConfig { Seed = seed, MapWidth = w, MapHeight = h });

    private static IEnumerable<(int Seed, int W, int H)> Cases()
    {
        for (int seed = 1; seed <= 25; seed++)
        {
            yield return (seed, 160, 96);
        }

        for (int seed = 1; seed <= 5; seed++)
        {
            yield return (seed, 640, 192);
        }

        yield return (3, 80, 24);
        yield return (4, 320, 48);
    }

    [Fact]
    public void GeneratedRoadsAreMostlyHighwaysWithStreetsLeavingTheInterchanges()
    {
        foreach (var (seed, w, h) in Cases())
        {
            var map = Generate(w, h, seed).Map;
            int highways = map.RoadCells.Count(i => map.RoadTypeAt(i % w, i / w)!.Name == DefaultRoads.KingsRoadName);
            int streets = map.RoadCells.Count(i => map.RoadTypeAt(i % w, i / w)!.Name == DefaultRoads.TrackName);
            Assert.True(highways > w / 2, $"seed {seed} {w}x{h}: only {highways} highway cells");
            Assert.True(highways > streets, $"seed {seed} {w}x{h}: {highways} highway vs {streets} street");
            if (w >= 160)
            {
                Assert.True(streets > 0, $"seed {seed} {w}x{h}: no street stubs");
            }
        }
    }

    [Fact]
    public void EveryGeneratedRoadReachesTheOutsideWorld()
    {
        foreach (var (seed, w, h) in Cases())
        {
            var game = Generate(w, h, seed);
            Assert.Equal(game.Map.RoadCount, game.Network.ConnectedRoadCount);
            Assert.True(game.Map.RoadCells.Any(i => game.Map.IsEdge(i % w, i / w)), $"seed {seed} {w}x{h}: no road reaches the edge");
        }
    }

    [Fact]
    public void RoadsNeverFormBlobsOrNearMisses()
    {
        foreach (var (seed, w, h) in Cases())
        {
            var map = Generate(w, h, seed).Map;
            bool Road(int x, int y) => map.HasRoad(x, y);

            foreach (int i in map.RoadCells)
            {
                int x = i % w, y = i / w;

                // No 2x2 blocks of road.
                Assert.False(Road(x + 1, y) && Road(x, y + 1) && Road(x + 1, y + 1), $"seed {seed}: 2x2 block at {x},{y}");

                // Diagonal neighbours must be joined through an orthogonal neighbour (a clean bend), never just touch.
                if (Road(x + 1, y + 1))
                {
                    Assert.True(Road(x + 1, y) || Road(x, y + 1), $"seed {seed}: diagonal touch at {x},{y}");
                }

                if (Road(x - 1, y + 1))
                {
                    Assert.True(Road(x - 1, y) || Road(x, y + 1), $"seed {seed}: diagonal touch at {x},{y}");
                }
            }
        }
    }

    [Fact]
    public void JunctionsAreCleanThreeOrFourWayInterchanges()
    {
        int totalJunctions = 0;
        foreach (var (seed, w, h) in Cases())
        {
            var map = Generate(w, h, seed).Map;
            var junctions = new List<Pos>();
            foreach (int i in map.RoadCells)
            {
                int x = i % w, y = i / w;
                int arms = CellRenderer.RoadMask(map, x, y) switch { 7 or 11 or 13 or 14 => 3, 15 => 4, _ => 0 };
                int neighbours = new[] { (0, -1), (1, 0), (0, 1), (-1, 0) }.Count(d => map.HasRoad(x + d.Item1, y + d.Item2));
                Assert.Equal(arms, neighbours >= 3 ? neighbours : 0);
                if (neighbours >= 3)
                {
                    junctions.Add(new Pos(x, y));
                }
            }

            // Interchanges are well separated: no two junction cells are close together.
            foreach (var a in junctions)
            {
                foreach (var b in junctions.Where(b => b != a))
                {
                    Assert.True(Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)) > 3, $"seed {seed}: junctions {a} and {b} too close");
                }
            }

            totalJunctions += junctions.Count;
        }

        Assert.True(totalJunctions > Cases().Count(), $"only {totalJunctions} junctions overall");
    }

    [Fact]
    public void HighwaysAreNotAGrid()
    {
        // Larger maps no longer get a lattice of parallel roads across the whole map: most rows and columns are empty of highway.
        var map = Generate(640, 192, 2).Map;
        int rowsWithLongRuns = 0;
        for (int y = 0; y < 192; y++)
        {
            int run = 0, longest = 0;
            for (int x = 0; x < 640; x++)
            {
                run = map.HasRoad(x, y) ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }

            if (longest >= 300)
            {
                rowsWithLongRuns++;
            }
        }

        Assert.True(rowsWithLongRuns <= 3, $"{rowsWithLongRuns} rows have a road almost all the way across");
    }

    [Fact]
    public void BridgesCarryHighwaysOverRivers()
    {
        int bridges = 0;
        foreach (var (seed, w, h) in Cases().Take(10))
        {
            var map = Generate(w, h, seed).Map;
            bridges += map.RoadCells.Count(i => !map.TerrainAt(i % w, i / w).Buildable);
        }

        Assert.True(bridges > 0, "no highway ever crossed a river");
    }

    [Fact]
    public void LongHighwaysDoNotRunCloseAndParallelToEachOther()
    {
        foreach (var (seed, w, h) in Cases())
        {
            var map = Generate(w, h, seed).Map;
            bool Highway(int x, int y) => map.RoadTypeAt(x, y)?.Name == DefaultRoads.KingsRoadName;

            // Horizontal runs: while a highway runs along a row, nothing else runs along the rows just above or below it,
            // except near its ends (where it meets an interchange and its own arms leave in other directions).
            for (int y = 0; y < h; y++)
            {
                int x = 0;
                while (x < w)
                {
                    if (!Highway(x, y) || !Highway(x + 1, y))
                    {
                        x++;
                        continue;
                    }

                    int start = x;
                    while (x + 1 < w && Highway(x + 1, y))
                    {
                        x++;
                    }

                    int end = x;
                    x++;
                    for (int dy = 1; dy <= 6; dy++)
                    {
                        for (int cx = start + 18; cx <= end - 18; cx++)
                        {
                            Assert.False(Highway(cx, y + dy) && Highway(cx + 1, y + dy), $"seed {seed} {w}x{h}: parallel highways {dy} rows apart at {cx},{y}");
                            Assert.False(Highway(cx, y - dy) && Highway(cx + 1, y - dy), $"seed {seed} {w}x{h}: parallel highways {dy} rows apart at {cx},{y}");
                        }
                    }
                }
            }

            // Vertical runs: the same, with the columns beside it (a column is half as wide as a row is tall).
            for (int x = 0; x < w; x++)
            {
                int y = 0;
                while (y < h)
                {
                    if (!Highway(x, y) || !Highway(x, y + 1))
                    {
                        y++;
                        continue;
                    }

                    int start = y;
                    while (y + 1 < h && Highway(x, y + 1))
                    {
                        y++;
                    }

                    int end = y;
                    y++;
                    for (int dx = 1; dx <= 12; dx++)
                    {
                        for (int cy = start + 9; cy <= end - 9; cy++)
                        {
                            Assert.False(Highway(x + dx, cy) && Highway(x + dx, cy + 1), $"seed {seed} {w}x{h}: parallel highways {dx} columns apart at {x},{cy}");
                            Assert.False(Highway(x - dx, cy) && Highway(x - dx, cy + 1), $"seed {seed} {w}x{h}: parallel highways {dx} columns apart at {x},{cy}");
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void InterchangesAreWellSeparated()
    {
        foreach (var (seed, w, h) in Cases())
        {
            var map = Generate(w, h, seed).Map;
            var junctions = new List<Pos>();
            foreach (int i in map.RoadCells)
            {
                int x = i % w, y = i / w;
                if (map.RoadTypeAt(x, y)!.Name == DefaultRoads.KingsRoadName &&
                    new[] { (0, -1), (1, 0), (0, 1), (-1, 0) }.Count(d => map.HasRoad(x + d.Item1, y + d.Item2)) >= 3)
                {
                    junctions.Add(new Pos(x, y));
                }
            }

            foreach (var a in junctions)
            {
                foreach (var b in junctions.Where(b => b != a))
                {
                    Assert.True(Math.Abs(a.X - b.X) > 32 || Math.Abs(a.Y - b.Y) > 16, $"seed {seed} {w}x{h}: interchanges {a} and {b} are too close");
                }
            }
        }
    }
}