using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class DiagonalHighwayTests
{
    private static string Glyphs(CityGame game, params (int X, int Y)[] cells) =>
        string.Concat(cells.Select(c => CellRenderer.Render(game, c.X, c.Y).Glyph));

    [Fact]
    public void ADiagonalIsADoubleLineStaircaseOfOrdinaryRoadCells()
    {
        var game = TestCity.Flat();
        var highway = game.Map.Content.Roads.Get("Highway");
        (int X, int Y)[] path = [(0, 10), (1, 10), (1, 11), (2, 11), (2, 12), (3, 12)];
        foreach (var (x, y) in path)
        {
            game.Map.SetRoad(x, y, highway);
        }

        game.Touch();
        Assert.Equal("═╗╚╗╚═", Glyphs(game, path));
        // It connects through the normal four-way links: the first cell is on the map edge.
        Assert.True(game.Network.IsConnected(game.Map, 3, 12));

        // The other direction.
        (int X, int Y)[] rising = [(0, 30), (1, 30), (1, 29), (2, 29), (2, 28), (3, 28)];
        foreach (var (x, y) in rising)
        {
            game.Map.SetRoad(x, y, highway);
        }

        game.Touch();
        Assert.Equal("═╝╔╝╔═", Glyphs(game, rising));
    }

    [Fact]
    public void NoSlashGlyphsAreUsedAnywhere()
    {
        for (int seed = 1; seed <= 30; seed++)
        {
            var game = CityGame.New(new GameConfig { Seed = seed });
            var map = game.Map;
            foreach (int i in map.RoadCells)
            {
                string glyph = CellRenderer.Render(game, i % map.Width, i / map.Width).Glyph;
                Assert.DoesNotContain(glyph, new[] { "╱", "╲", "╳" });
            }
        }
    }

    [Fact]
    public void StaircasesAppearInGeneratedMapsAndEveryRoadStillReachesTheEdge()
    {
        int stairs = 0;
        for (int seed = 1; seed <= 30; seed++)
        {
            var game = CityGame.New(new GameConfig { Seed = seed });
            var map = game.Map;
            foreach (int i in map.RoadCells)
            {
                int x = i % map.Width, y = i / map.Width;

                // A stair step: a down-right corner followed by an up-right corner just below it, or the mirror image.
                string here = CellRenderer.Render(game, x, y).Glyph;
                string next = CellRenderer.Render(game, x + 1, y).Glyph;
                if ((here == "╚" && next == "╗") || (here == "╔" && next == "╝"))
                {
                    stairs++;
                }
            }

            Assert.Equal(map.RoadCount, game.Network.ConnectedRoadCount);
        }

        Assert.True(stairs > 20, $"only {stairs} staircase steps across 30 maps");
    }

    [Fact]
    public void BridgesAreAlwaysStraight()
    {
        for (int seed = 1; seed <= 30; seed++)
        {
            var map = CityGame.New(new GameConfig { Seed = seed }).Map;
            foreach (int i in map.RoadCells)
            {
                int x = i % map.Width, y = i / map.Width;
                if (!map.TerrainAt(x, y).Buildable)
                {
                    Assert.Contains(CellRenderer.RoadMask(map, x, y), new[] { 1, 4, 5, 2, 8, 10 });
                }
            }
        }
    }

    [Fact]
    public void TheDefaultMapHasASparseNetworkLeavingRoomToBuild()
    {
        for (int seed = 1; seed <= 20; seed++)
        {
            var map = CityGame.New(new GameConfig { Seed = seed }).Map;
            Assert.True(map.RoadCount < map.Width * map.Height * 0.05, $"seed {seed}: {map.RoadCount} road cells");
        }
    }
}
public class MinimapAndSessionTests
{
    private static GameSession NewSession()
    {
        var s = new GameSession(TestCity.Flat(), Path.Combine(Path.GetTempPath(), "tc-" + Guid.NewGuid().ToString("N") + ".json"));
        s.SetViewport(80, 24);
        return s;
    }

    [Fact]
    public void MessagesExpireAndEmptyMessagesAreNeverShown()
    {
        var s = NewSession();
        s.SetMessage("Built something");
        Assert.True(s.MessageVisible);
        s.SetMessage(string.Empty);
        Assert.False(s.MessageVisible);
    }

    [Fact]
    public void ZoomingDoesNotLeaveAMessageBehind()
    {
        var s = NewSession();
        s.SetMessage(string.Empty);
        s.ZoomBy(-1);
        Assert.False(s.MessageVisible);
    }

    [Fact]
    public void CellSummaryIsOneLineWithCoordinatesTerrainAndRoadType()
    {
        var game = TestCity.Flat();
        game.Map.SetRoad(30, 10, game.Map.Content.Roads.Get("Highway"));
        game.Touch();
        string road = CellInspector.Summary(game, new Pos(30, 10));
        Assert.StartsWith("(30,10) Grass", road);
        Assert.Contains("Highway (NOT connected)", road);
        Assert.DoesNotContain('\n', road);

        game.Designate(new CellRect(5, 21, 1, 1), ZoneType.Residential);
        Assert.Contains("Residential zone", CellInspector.Summary(game, new Pos(5, 21)));
    }

    [Fact]
    public void MinimapDrawsHighwaysFaintlyAndTheCityBrightly()
    {
        var game = TestCity.Flat();
        var highway = game.Map.Content.Roads.Get("Highway");
        for (int x = 0; x < game.Map.Width; x++)
        {
            game.Map.SetRoad(x, 10, highway);
        }

        game.Designate(new CellRect(100, 30, 12, 6), ZoneType.Industrial);
        game.Touch();

        var image = new MinimapImage();
        image.Update(game, 34, 20);

        var land = image[2, 15];
        var road = image[10, 10 * 20 / game.Map.Height];
        var zone = image[100 * 34 / 160 + 1, 31 * 20 / game.Map.Height];

        static int Distance(Rgb a, Rgb b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

        Assert.NotEqual(highway.Foreground, road);
        Assert.NotEqual(land, road);
        Assert.True(Distance(land, road) < 140, "the highway should be a faint line");
        Assert.True(Distance(land, zone) > Distance(land, road), "the zone should stand out more than the highway");
    }

    [Fact]
    public void ANarrowRiverShowsOnTheMinimapEvenWhenHillsSurroundIt()
    {
        var game = TestCity.Flat();
        var map = game.Map;
        var terrains = map.Content.Terrains;

        // Hills everywhere, with a river two rows wide across the middle (it straddles minimap pixel rows).
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                map.SetTerrain(x, y, terrains.Get("Hill"));
            }
        }

        for (int y = 40; y <= 41; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                map.SetTerrain(x, y, terrains.Get("Water"));
            }
        }

        game.Touch();
        var image = new MinimapImage();
        image.Update(game, 34, 20);

        int riverPixels = 0;
        for (int px = 0; px < 34; px++)
        {
            for (int py = 0; py < 20; py++)
            {
                // Rows 40-41 fall in pixel row 40 * 20 / 96 = 8 (and 41 in 8 too, or the next one).
                if (py is >= 8 and <= 9 && image[px, py] == image[0, 8])
                {
                    riverPixels++;
                }
            }
        }

        var hillPixel = image[3, 2];
        Assert.NotEqual(hillPixel, image[3, 8]);
        Assert.True(riverPixels >= 34, $"the river should run unbroken across all 34 pixel columns, found {riverPixels}");

        // The river is a clearly different, brighter blue than the hill shade.
        Assert.True(image[3, 8].B > hillPixel.B + 60);
    }

    [Fact]
    public void MinimapUsesOnlyAFewTerrainColours()
    {
        var game = CityGame.New(new GameConfig { Seed = 3 });
        var image = new MinimapImage();
        image.Update(game, 34, 20);

        var colours = new HashSet<Rgb>();
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 34; x++)
            {
                colours.Add(image[x, y]);
            }
        }

        // Land, water, a faint hill shade, plus the faint road tint over each of them.
        Assert.True(colours.Count <= 8, $"{colours.Count} colours");
    }

    [Fact]
    public void ThePersistedGameKeepsItsDayAndPlaysAtTheCurrentSpeeds()
    {
        var game = TestCity.Flat();
        for (int i = 0; i < 12; i++)
        {
            game.AdvanceDay();
        }

        Assert.Equal(5, game.Day);
        Assert.Equal(1, game.Week);

        var root = System.Text.Json.Nodes.JsonNode.Parse(TermCity.Core.Persistence.SaveGameStore.Serialize(game))!.AsObject();
        var config = root["Config"]!.AsObject();
        config["SlowSecondsPerWeek"] = 3.0;
        config["MediumSecondsPerWeek"] = 1.5;
        config["FastSecondsPerWeek"] = 0.5;
        var loaded = TermCity.Core.Persistence.SaveGameStore.Deserialize(root.ToJsonString());

        Assert.Equal(5, loaded.Day);
        Assert.Equal(1, loaded.Week);
        Assert.Equal(new GameConfig().MediumSecondsPerWeek, loaded.Config.MediumSecondsPerWeek);
        Assert.Equal(new GameConfig().FastSecondsPerWeek, loaded.Config.FastSecondsPerWeek);
    }

    [Fact]
    public void CursorAndSelectionChangesDoNotWakeTheWholeInterface()
    {
        var s = new GameSession(TestCity.Flat(), Path.Combine(Path.GetTempPath(), "tc-" + Guid.NewGuid().ToString("N") + ".json"));
        s.SetViewport(80, 24);
        int everything = 0, selection = 0;
        s.Changed += () => everything++;
        s.SelectionChanged += () => selection++;

        s.PlaceCursor(new Pos(60, 40));
        s.BeginDrag(new Pos(60, 40));
        s.UpdateDrag(new Pos(65, 44));
        s.EndSelection();
        s.ClearSelection();
        s.MoveCursor(1, 0);

        Assert.Equal(0, everything);
        Assert.True(selection >= 6, $"selection={selection}");

        // A message, on the other hand, does change what the whole interface shows.
        s.SetMessage("hello");
        Assert.Equal(1, everything);
    }
}