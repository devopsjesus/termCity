using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class SettlementToolTests
{
    [Fact]
    public void EnclosureFollowsPointerOnlyAfterFirstCornerAndCommitsAtomically()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var session = new GameSession(game);
        int money = game.Money;
        session.BeginSettlementPlacement();
        Assert.True(session.MapNavigationAllowed);
        Assert.True(session.FixedPlacementToolActive);
        session.MoveFixedPlacement(new(30, 30));
        Assert.Null(session.FixedPlacementError);
        Assert.True(session.ConfirmPreview().Success);
        Assert.Null(game.Settlement);
        session.MoveFixedPlacement(new(57, 53));
        Assert.Equal(new CellRect(30, 30, 28, 24), session.Preview!.Area);
        Assert.All(session.Preview.Cells!, p => Assert.True(p.X is 30 or 57 || p.Y is 30 or 53));
        Assert.Equal(money, game.Money);
        Assert.Null(session.FixedPlacementError);
        Assert.True(session.ConfirmPreview().Success, session.Message);
        Assert.NotNull(game.Settlement);
        Assert.Null(session.Preview);
        Assert.Equal(1, session.UndoCount);
        Assert.True(game.Money < money);
    }

    [Fact]
    public void InvalidAndCancelledEnclosuresDoNotSpendGoldOrLeaveWalls()
    {
        var game = TestCity.Flat();
        var session = new GameSession(game);
        session.ShowGarrisonMenu();
        var menu = session.Prompt;
        session.SelectPrompt(0);
        Assert.Null(session.Prompt);
        session.MoveFixedPlacement(new(30, 30));
        session.ConfirmPreview();
        session.MoveFixedPlacement(new(31, 31));
        Assert.NotNull(session.FixedPlacementError);
        int money = game.Money;
        Assert.False(session.ConfirmPreview().Success);
        Assert.NotNull(session.Preview);
        Assert.Null(game.Settlement);
        Assert.Equal(money, game.Money);
        session.CancelPreview();
        Assert.Same(menu, session.Prompt);
        Assert.Null(session.Preview);
        session.ClosePrompt();
        session.BeginSettlementPlacement();
        session.MoveFixedPlacement(new(40, 40));
        Assert.Single(session.Preview!.Cells!);
    }

    [Fact]
    public void MotteCatalogEntryStartsTheEnclosureToolNotASmallBuilding()
    {
        var game = TestCity.Flat();
        var session = new GameSession(game);
        session.PreviewBuilding(game.Map.Content.Buildings.Get("Motte and Bailey"));
        Assert.True(session.SettlementToolActive);
        Assert.False(session.BuildingToolActive);
        Assert.Equal(PlacementKind.Settlement, session.Preview!.Kind);
    }

    [Fact]
    public void KeyboardResizesEnclosureAndPausedUndoRestoresTheWholeFounding()
    {
        var game = TestCity.Flat();
        game.Paused = true;
        var session = new GameSession(game);
        session.BeginSettlementPlacement();
        session.MoveFixedPlacement(new(30, 30));
        session.ConfirmPreview();
        session.MoveCursor(27, 23);
        Assert.Equal(new CellRect(30, 30, 28, 24), session.Preview!.Area);
        Assert.True(session.ConfirmPreview().Success, session.Message);
        Assert.NotNull(game.Settlement);
        session.RequestUndo();
        session.SelectPrompt(0);
        Assert.Null(session.Game.Settlement);
        Assert.Equal(game.Config.StartingMoney, session.Game.Money);
    }

    [Theory]
    [InlineData("--debug-settlement")]
    [InlineData("--debug-siege")]
    public void SettlementDebugStartsAreExplicitAndCannotModifyLoadedCities(string flag)
    {
        Assert.False(GodotOptions.Parse([]).DebugSettlement);
        Assert.False(GodotOptions.Parse([]).DebugSiege);
        if (CityGame.DebugToolsAvailable)
        {
            var options = GodotOptions.Parse([flag, "--seed", "42"]);
            Assert.Equal(flag == "--debug-settlement", options.DebugSettlement);
            Assert.Equal(flag == "--debug-siege", options.DebugSiege);
        }
        else Assert.Throws<ArgumentException>(() => GodotOptions.Parse([flag]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse([flag, "--load"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse([flag, "--size", "SF"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse([flag, "--debug-starter"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse([flag, "--smoke-test"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse([flag, "--dump-map"]));
    }

    [Fact]
    public void SettlementSmokeIsAnIsolatedMode()
    {
        Assert.True(GodotOptions.Parse(["--settlement-smoke-test"]).SettlementSmokeTest);
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--settlement-smoke-test", "--perspective-smoke-test"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--settlement-smoke-test", "--load"]));
    }
}
