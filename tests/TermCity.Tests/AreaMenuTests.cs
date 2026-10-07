using TermCity.Core.Buildings;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class AreaMenuTests
{
    private static GameSession Session()
    {
        var session = new GameSession(TestCity.Flat());
        session.Game.Paused = true;
        session.PlaceCursor(new Pos(10, 19));
        return session;
    }

    [Theory]
    [InlineData(0, ZoneType.Residential)]
    [InlineData(1, ZoneType.Commercial)]
    [InlineData(2, ZoneType.Industrial)]
    public void ZoneMenuAppliesEachRegisteredZoneToTheSelection(int choice, ZoneType zone)
    {
        var session = Session();
        session.BeginDrag(new Pos(10, 19));
        session.UpdateDrag(new Pos(12, 19));
        session.EndSelection();
        int money = session.Game.Money;
        session.ShowAreaMenu();
        Assert.Contains("3 cells", session.Prompt!.Text);
        session.SelectPrompt(0);
        session.SelectPrompt(choice);
        Assert.Null(session.Prompt);
        Assert.Equal(money, session.Game.Money);
        foreach (int x in Enumerable.Range(10, 3))
            Assert.Equal(zone, session.Game.Map.ZoneAt(x, 19));
    }

    [Fact]
    public void DezoneAndDemolishMenuChoicesUseSessionActions()
    {
        var session = Session();
        session.Zone(ZoneType.Residential);
        session.ShowAreaMenu();
        session.SelectPrompt(0);
        session.SelectPrompt(3);
        Assert.Equal(ZoneType.None, session.Game.Map.ZoneAt(10, 19));
        session.Zone(ZoneType.Residential);
        session.ShowAreaMenu();
        session.SelectPrompt(3);
        Assert.Null(session.Prompt);
        Assert.Equal(PlacementKind.Demolish, session.Preview!.Kind);
        Assert.Equal(ZoneType.Residential, session.Game.Map.ZoneAt(10, 19));
        Assert.True(session.ConfirmPreview().Success);
        Assert.Equal(ZoneType.None, session.Game.Map.ZoneAt(10, 19));
        Assert.True(session.CanUndo);
    }

    [Fact]
    public void RoadMenuOffersEveryPlayerRoadAndItsLineTool()
    {
        var session = Session();
        var roads = session.Game.Map.Content.Roads.Where(r => r.PlayerPlaceable).OrderBy(r => r.Rank).ToArray();
        session.ShowAreaMenu();
        session.SelectPrompt(1);
        Assert.Equal(roads.Length * 2 + 1, session.Prompt!.Choices.Count);
        for (int i = 0; i < roads.Length; i++)
        {
            session.ShowAreaMenu();
            session.SelectPrompt(1);
            Assert.Contains(Fmt.Money(session.Game.QuoteRoad(session.ActiveArea, roads[i]).Cost),
                session.Prompt!.Choices[i * 2].Label);
            session.SelectPrompt(i * 2);
            Assert.Null(session.Prompt);
            Assert.Same(roads[i], session.Preview!.Road);
            session.CancelPreview();
            session.ShowAreaMenu();
            session.SelectPrompt(1);
            session.SelectPrompt(i * 2 + 1);
            Assert.True(session.RoadToolActive);
            Assert.Same(roads[i], session.Preview!.Road);
            session.MoveCursor(2, 0);
            Assert.Equal(3, session.Preview.Area.Width);
            session.CancelPreview();
            session.PlaceCursor(new Pos(10, 19));
        }
    }

    [Fact]
    public void BuildingMenuHandlesEmptyRegistryAndPlayerExtensions()
    {
        var session = Session();
        session.ShowAreaMenu();
        session.SelectPrompt(2);
        Assert.Equal("Choose a building to preview.", session.Prompt!.Text);
        int civic = session.Game.Map.Content.Buildings.Count(b => b.PlayerPlaceable);
        Assert.True(civic > 0);
        Assert.Equal(civic + 1, session.Prompt.Choices.Count);
        session.SelectPrompt(civic);
        Assert.Equal("Area menu", session.Prompt!.Title);

        var building = session.Game.Map.Content.Buildings.Register(new BuildingType
        {
            Name = "Shrine", Glyphs = ["+"], Foreground = Rgb.Hex(0xffffff),
            Cost = 1_000, PlayerPlaceable = true,
        });
        session.SelectPrompt(2);
        Assert.Contains("Shrine", session.Prompt!.Choices[civic].Label);
        session.SelectPrompt(civic);
        Assert.Null(session.Prompt);
        Assert.Same(building, session.Preview!.Building);
        Assert.Equal(1_000, session.Preview.Quote.Cost);
        Assert.True(session.ConfirmPreview().Success);
        Assert.Same(building, session.Game.Map.BuildingAt(10, 19));
    }

    [Fact]
    public void AreaMenuCancelAndCityNavigationDoNotMutateCity()
    {
        var session = Session();
        session.SelectCell(new Pos(10, 19));
        session.ShowAreaMenu();
        session.SelectPrompt(4);
        Assert.Null(session.Selection);
        Assert.Null(session.Prompt);
        session.ShowAreaMenu();
        session.SelectPrompt(5);
        Assert.Equal("City menu", session.Prompt!.Title);
        Assert.Equal(0, session.Game.Stats.Residential.Zoned);
    }
}
