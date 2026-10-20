using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Roads;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class TrafficCircleTests
{
    [Fact]
    public void CircleBuildsOneAtomicCobbledRingWithAnOpenCenterAndPersistsAsRoads()
    {
        var game = TestCity.Flat();
        var template = RoadTemplate.TrafficCircle;
        var origin = new Pos(10, 5);
        var quote = game.QuoteRoadTemplate(template, origin);
        int money = game.Money, roads = game.Map.RoadCount;
        game.Map.SetFeature(12, 7, game.Map.Content.Features.Get("Tree"));
        Assert.True(game.BuildRoadTemplate(template, origin).Success);
        Assert.Equal(16, quote.Cells);
        Assert.Equal(money - quote.Cost, game.Money);
        Assert.Equal(roads + 16, game.Map.RoadCount);
        foreach (var p in template.Cells(origin))
            Assert.Equal(DefaultRoads.CobbledName, game.Map.RoadTypeAt(p.X, p.Y)!.Name);
        for (int y = 6; y < 9; y++)
            for (int x = 11; x < 14; x++)
            {
                Assert.False(game.Map.HasRoad(x, y));
                Assert.Null(game.Map.FeatureAt(x, y));
            }
        var paths = game.Network.DrawnPaths.Where(path => path.Type.Name == DefaultRoads.CobbledName).ToArray();
        Assert.Contains(paths, path => path.Closed);
        var layout = new RoadMeshLayout(game.Map, paths);
        Assert.Null(layout.RoadAt(new(12.5f, 7.5f)));
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(game.Map.RoadLayer, loaded.Map.RoadLayer);
        Assert.Equal(game.Map.RoadTypeLayer, loaded.Map.RoadTypeLayer);
    }

    [Theory]
    [InlineData("water")]
    [InlineData("building")]
    [InlineData("zone")]
    [InlineData("center road")]
    [InlineData("map edge")]
    [InlineData("money")]
    public void InvalidCirclePlacementCannotBuildAPartialRing(string obstruction)
    {
        var game = TestCity.Flat();
        var origin = new Pos(10, 5);
        switch (obstruction)
        {
            case "water": game.Map.SetTerrain(12, 7, game.Map.Content.Terrains.Get("Water")); break;
            case "building": game.Map.SetBuilding(10, 5, game.Map.Content.Buildings.Get("Town Well")); break;
            case "zone": game.Map.SetZone(11, 6, TermCity.Core.World.ZoneType.Residential); break;
            case "center road": game.Map.SetRoad(12, 7, true); break;
            case "map edge": origin = new(game.Map.Width - 2, game.Map.Height - 2); break;
            case "money": game.Money = 0; break;
        }
        game.Touch();
        string before = SaveGameStore.Serialize(game);
        Assert.False(game.BuildRoadTemplate(RoadTemplate.TrafficCircle, origin).Success);
        Assert.Equal(before, SaveGameStore.Serialize(game));
    }

    [Fact]
    public void CirclePlacementPreviewMovesAndCanBeUndoneAsOneAction()
    {
        var game = TestCity.Flat();
        game.Paused = true;
        var session = new GameSession(game);
        session.PlaceCursor(new(10, 5));
        session.PreviewRoadTemplate(RoadTemplate.TrafficCircle);
        Assert.True(session.FixedPlacementToolActive);
        Assert.Equal(16, session.Preview!.Cells!.Count);
        session.MoveCursor(1, 0);
        Assert.Equal(11, session.Preview.Area.X);
        Assert.True(session.ConfirmPreview().Success);
        Assert.True(session.FixedPlacementToolActive);
        Assert.Same(RoadTemplate.TrafficCircle, session.Preview!.Template);
        Assert.Equal(1, session.UndoCount);
        session.CancelPreview();
        Assert.False(session.FixedPlacementToolActive);
        session.RequestUndo();
        session.SelectPrompt(0);
        Assert.False(session.Game.Map.HasRoad(11, 5));
    }

    [Fact]
    public void CircleAppearsInTheRoadMenuWithNormalRoadStatistics()
    {
        var session = new GameSession(TestCity.Flat());
        session.ShowAreaMenu();
        session.SelectPrompt(1);
        var choice = Assert.Single(session.Prompt!.Choices, row => row.Label == "Traffic Circle");
        Assert.Contains("5x5", choice.Help!.Details);
        Assert.Contains("Traffic capacity:", choice.Help.Details);
        choice.Select();
        Assert.Equal(PlacementKind.RoadTemplate, session.Preview!.Kind);
    }
}
