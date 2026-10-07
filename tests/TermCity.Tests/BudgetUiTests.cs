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
        var fire = session.Prompt!.Choices.First(c => c.Label.StartsWith("Fire funding"));
        fire.Select();
        Assert.Equal(0.75, session.Game.Budget.Funding(TermCity.Core.Buildings.ServiceKind.Fire), 3);
        Assert.NotNull(session.Prompt);
        Assert.Contains(session.Prompt!.Choices, c => c.Label.StartsWith("Fire funding 75%"));
    }

    [Fact]
    public void BudgetMenuTaxesAndLoans()
    {
        var session = Session();
        session.ShowBudgetMenu();
        double before = session.Game.Taxes.Residential;
        session.Prompt!.Choices.First(c => c.Label.StartsWith("Residential tax")).Select();
        Assert.True(session.Game.Taxes.Residential > before);
        session.Game.SetTax(ZoneType.Commercial, 5);
        Assert.Equal(0.3, session.Game.Taxes.Commercial, 3);
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
