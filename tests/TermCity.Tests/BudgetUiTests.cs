using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Tests;

public class BudgetUiTests
{
    private static GameSession Session(CityRules rules = CityRules.Full) =>
        new(TestCity.Flat(1, new GameConfig { StartingMoney = 1_000_000 }, rules));

    [Fact]
    public void BudgetMenuCyclesFundingAndRefreshes()
    {
        var session = Session();
        session.ShowBudgetMenu();
        var fire = session.Prompt!.Choices.First(c => c.Label.StartsWith("Fire watch"));
        fire.Select();
        Assert.Equal(0.75, session.Game.Budget.Funding(TermCity.Core.Buildings.ServiceKind.Fire), 3);
        Assert.NotNull(session.Prompt);
        Assert.Contains(session.Prompt!.Choices, c => c.Label.StartsWith("Fire watch") && c.Cells![0] == 0.75.ToString("P0"));
    }

    [Fact]
    public void BudgetMenuTaxesAndLoans()
    {
        var session = Session();
        session.ShowBudgetMenu();
        double before = session.Game.Taxes.Residential;
        session.Prompt!.Choices.First(c => c.Label.StartsWith("Hearth tithe and rents")).Select();
        Assert.True(session.Game.Taxes.Residential > before);
        session.Game.SetTax(ZoneType.Commercial, 5);
        Assert.Equal(0.3, session.Game.Taxes.Commercial, 3);
    }

    [Fact]
    public void TaxChangesMarkAPausedSavedSessionDirtyWithoutChangingTheMap()
    {
        string path = Path.Combine(Path.GetTempPath(), "termcity-tax-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var game = TestCity.Flat();
            game.Paused = true;
            var session = new GameSession(game, path);
            Assert.True(session.QuickSave());
            Assert.False(session.HasUnsavedChanges);
            int mapVersion = game.MapVersion;
            var network = game.Network;
            int changes = 0;
            game.Changed += () => changes++;

            game.SetTax(ZoneType.Residential, 0.1);

            Assert.True(session.HasUnsavedChanges);
            Assert.Equal(1, changes);
            Assert.Equal(mapVersion, game.MapVersion);
            Assert.Same(network, game.Network);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ClassicBudgetMenuExplainsItself()
    {
        var session = Session(CityRules.Classic);
        session.ShowBudgetMenu();
        Assert.Contains("Classic", session.Prompt!.Text);
    }

    [Fact]
    public void ReportsDescribeTheCity()
    {
        var game = Session().Game;
        Assert.Contains("Mood", CityReport.Overview(game));
        Assert.Contains("Coverage", CityReport.Health(game));
        Assert.DoesNotContain("Mood", CityReport.Overview(Session(CityRules.Classic).Game));
    }
}

public class OverlayTests
{
    [Fact]
    public void OverlaysCycleAndTintOnlyUnderFullRules()
    {
        var game = CityGame.New(new GameConfig { Scenario = CityScenario.Chicago, MapWidth = 640, MapHeight = 384 });
        var session = new GameSession(game);
        for (int i = 0; i < 3; i++) session.CycleOverlay();

        Assert.Equal(MapOverlay.Fire, session.Overlay);
        bool tinted = false;
        for (int y = 100; y < 160 && !tinted; y++)
        {
            for (int x = 200; x < 260 && !tinted; x++)
            {
                tinted = CellRenderer.Render(game, x, y, MapOverlay.Fire).Background != CellRenderer.Render(game, x, y).Background;
            }
        }

        Assert.True(tinted);
        for (int i = 0; i < 25; i++) session.CycleOverlay();
        Assert.Equal(MapOverlay.Off, session.Overlay); // 3 + 25 steps is two full laps of the 14 modes
        var classic = new GameSession(TestCity.Flat());
        classic.CycleOverlay();
        Assert.Equal(MapOverlay.Off, classic.Overlay);
    }
}
